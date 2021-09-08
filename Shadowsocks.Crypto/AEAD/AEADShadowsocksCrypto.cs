using CryptoBase;
using CryptoBase.Abstractions.SymmetricCryptos;
using CryptoBase.Digests;
using CryptoBase.KDF;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;

// ReSharper disable VirtualMemberCallInConstructor
namespace Shadowsocks.Crypto.AEAD
{
	public abstract class AEADShadowsocksCrypto : IAEADShadowsocksCrypto
	{
		public byte[] Key { get; }
		public byte[] SessionKey { get; }
		public abstract int KeyLength { get; }
		protected Span<byte> MasterKeySpan => Key.AsSpan(0, KeyLength);
		protected Span<byte> SessionKeySpan => SessionKey.AsSpan(0, KeyLength);

		public byte[] Nonce { get; }
		public virtual int NonceLength => 12;
		protected Span<byte> NonceSpan => Nonce.AsSpan(0, NonceLength);

		public byte[] Salt { get; }
		public abstract int SaltLength { get; }
		protected Span<byte> SaltSpan => Salt.AsSpan(0, SaltLength);

		public const int PayloadLengthBytes = 2;
		public const int PayloadLengthLimit = 0x3FFF;
		public const int TagLength = 16;
		public const int MaxSaltLength = 32;
		public const int ChunkOverheadSize = TagLength * 2 + PayloadLengthBytes;
		public const int MaxChunkSize = PayloadLengthLimit + ChunkOverheadSize;

		private IAEADCrypto? _crypto;

		private bool _isFirstPacket;

		protected AEADShadowsocksCrypto(string password)
		{
			_isFirstPacket = true;

			Key = ArrayPool<byte>.Shared.Rent(KeyLength);
			SessionKey = ArrayPool<byte>.Shared.Rent(KeyLength);
			Nonce = ArrayPool<byte>.Shared.Rent(NonceLength);
			Salt = ArrayPool<byte>.Shared.Rent(SaltLength);

			MasterKeySpan.SsDeriveKey(password);
			NonceSpan.Clear();
		}

		[MemberNotNull(nameof(_crypto))]
		private void InitCipher()
		{
			_crypto?.Dispose();
			_crypto = CreateSessionCrypto();
		}

		private IAEADCrypto CreateSessionCrypto()
		{
			var sessionKey = SessionKeySpan;
			Hkdf.DeriveKey(
				DigestType.Sha1,
				MasterKeySpan,
				sessionKey,
				SaltSpan,
				ShadowsocksCrypto.InfoBytes
			);
			return CreateCrypto(sessionKey);
		}

		protected abstract IAEADCrypto CreateCrypto(ReadOnlySpan<byte> key);

		private void CipherEncrypt(IAEADCrypto crypto, ReadOnlySpan<byte> source, Span<byte> destination, ref int processLength, ref int outLength)
		{
			crypto.Encrypt(NonceSpan, source, destination[..source.Length], destination.Slice(source.Length, TagLength));
			processLength += source.Length;
			outLength += source.Length + TagLength;
		}

		/// <summary>
		/// 返回 false 表示需要更多的 source/destination
		/// </summary>
		private bool EncryptChunk(ReadOnlySpan<byte> source, Span<byte> destination, ref int processLength, ref int outLength)
		{
			var length = (ushort)source.Length;
			if (destination.Length < ChunkOverheadSize + length)
			{
				return false;
			}

			Span<byte> payloadLengthBuffer = stackalloc byte[PayloadLengthBytes];
			BinaryPrimitives.WriteUInt16BigEndian(payloadLengthBuffer, length);

			var nonce = NonceSpan;

			var unused = 0;
			CipherEncrypt(
				_crypto!,
				payloadLengthBuffer,
				destination,
				ref unused,
				ref outLength
			);
			nonce.Increment();

			CipherEncrypt(
				_crypto!,
				source,
				destination[(PayloadLengthBytes + TagLength)..],
				ref processLength,
				ref outLength
			);
			nonce.Increment();

			return true;
		}

		private bool DecryptChunk(ref ReadOnlySequence<byte> source, Span<byte> destination, out int outLength)
		{
			outLength = 0;

			if (source.Length <= ChunkOverheadSize)
			{
				return false;
			}

			// Decrypt PayloadLength
			var nonce = NonceSpan;
			Span<byte> tagBuffer = stackalloc byte[TagLength];
			Span<byte> payloadLengthBuffer = stackalloc byte[PayloadLengthBytes];

			if (source.IsSingleSegment || source.FirstSpan.Length >= PayloadLengthBytes + TagLength)
			{
				var encryptPayloadLength = source.FirstSpan[..PayloadLengthBytes];
				var tag = source.FirstSpan.Slice(PayloadLengthBytes, TagLength);

				_crypto!.Decrypt(nonce, encryptPayloadLength, tag, payloadLengthBuffer);
			}
			else
			{
				Span<byte> encryptPayloadLengthBuffer = stackalloc byte[PayloadLengthBytes];
				source.Slice(0, PayloadLengthBytes).CopyTo(encryptPayloadLengthBuffer);
				source.Slice(PayloadLengthBytes, TagLength).CopyTo(tagBuffer);

				_crypto!.Decrypt(nonce, encryptPayloadLengthBuffer, tagBuffer, payloadLengthBuffer);
			}
			var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(payloadLengthBuffer);

			if (payloadLength > PayloadLengthLimit)
			{
				throw new InvalidDataException($@"Invalid payloadLength: {payloadLength}");
			}

			if (source.Length - ChunkOverheadSize < payloadLength
				|| destination.Length < payloadLength)
			{
				return false;
			}

			nonce.Increment();

			// Decrypt Payload
			var remain = source.Slice(PayloadLengthBytes + TagLength);
			if (remain.IsSingleSegment || remain.FirstSpan.Length >= payloadLength)
			{
				var payloadBufferSpan = remain.FirstSpan[..payloadLength];
				remain.Slice(payloadLength, TagLength).CopyTo(tagBuffer);
				_crypto.Decrypt(nonce, payloadBufferSpan, tagBuffer, destination[..payloadLength]);
			}
			else
			{
				var payloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
				try
				{
					var payloadBufferSpan = payloadBuffer.AsSpan(0, payloadLength);
					remain.Slice(0, payloadLength).CopyTo(payloadBufferSpan);
					remain.Slice(payloadLength, TagLength).CopyTo(tagBuffer);
					_crypto.Decrypt(nonce, payloadBufferSpan, tagBuffer, destination[..payloadLength]);
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(payloadBuffer);
				}
			}

			nonce.Increment();

			outLength = payloadLength;
			source = source.Slice(ChunkOverheadSize + payloadLength);
			return true;
		}

		public void EncryptTCP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength)
		{
			processLength = 0;
			outLength = 0;

			if (_isFirstPacket)
			{
				var salt = SaltSpan;
				RandomNumberGenerator.Fill(salt);
				InitCipher();
				salt.CopyTo(destination);
				outLength += SaltLength;

				_isFirstPacket = false;
			}

			while (processLength < source.Length)
			{
				var remain = source[processLength..];
				var chunkLength = Math.Min(PayloadLengthLimit, remain.Length);

				if (!EncryptChunk(
					remain[..chunkLength],
					destination[outLength..],
					ref processLength,
					ref outLength
				))
				{
					return;
				}
			}
		}

		public int DecryptTCP(ref ReadOnlySequence<byte> source, Span<byte> destination)
		{
			var outLength = 0;

			if (_isFirstPacket)
			{
				if (source.Length <= SaltLength)
				{
					return 0;
				}

				source.Slice(0, SaltLength).CopyTo(SaltSpan);
				InitCipher();
				source = source.Slice(SaltLength);

				_isFirstPacket = false;
			}

			while (DecryptChunk(ref source, destination[outLength..], out var o))
			{
				outLength += o;
			}

			return outLength;
		}

		public int EncryptUDP(ReadOnlySpan<byte> source, Span<byte> destination)
		{
			var processLength = 0;

			var salt = SaltSpan;
			RandomNumberGenerator.Fill(salt);
			using var crypto = CreateSessionCrypto();
			salt.CopyTo(destination);
			var outLength = SaltLength;

			CipherEncrypt(
				crypto,
				source,
				destination[SaltLength..],
				ref processLength,
				ref outLength
			);

			return outLength;
		}

		public int DecryptUDP(ReadOnlySpan<byte> source, Span<byte> destination)
		{
			source[..SaltLength].CopyTo(SaltSpan);
			using var crypto = CreateSessionCrypto();

			var realLength = source.Length - TagLength - SaltLength;
			crypto.Decrypt(
				NonceSpan,
				source.Slice(SaltLength, realLength),
				source.Slice(SaltLength + realLength, TagLength),
				destination[..realLength]
			);

			return realLength;
		}

		public static int GetBufferSize(int sourceLength)
		{
			var blocks = sourceLength / PayloadLengthLimit;
			return MaxSaltLength + blocks * MaxChunkSize + sourceLength % PayloadLengthLimit + ChunkOverheadSize;
		}

		public virtual void Dispose()
		{
			ArrayPool<byte>.Shared.Return(Key);
			ArrayPool<byte>.Shared.Return(SessionKey);
			ArrayPool<byte>.Shared.Return(Nonce);
			ArrayPool<byte>.Shared.Return(Salt);

			_crypto?.Dispose();
		}
	}
}
