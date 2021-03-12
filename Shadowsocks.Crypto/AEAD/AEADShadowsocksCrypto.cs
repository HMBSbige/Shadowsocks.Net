using CryptoBase;
using CryptoBase.Abstractions.SymmetricCryptos;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

// ReSharper disable VirtualMemberCallInConstructor
namespace Shadowsocks.Crypto.AEAD
{
	public abstract class AEADShadowsocksCrypto : IAEADShadowsocksCrypto
	{
		public int AddressBufferLength { get; set; }

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
		public const int ReceiveSize = 2048;
		public const int BufferSize = ReceiveSize + MaxChunkSize + MaxSaltLength;

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

		private void InitCipher()
		{
			_crypto?.Dispose();
			_crypto = CreateSessionCrypto();
		}

		private IAEADCrypto CreateSessionCrypto()
		{
			var sessionKey = SessionKeySpan;
			HKDF.DeriveKey(HashAlgorithmName.SHA1, MasterKeySpan, sessionKey, SaltSpan, ShadowsocksCrypto.InfoBytes);
			return CreateCrypto(sessionKey);
		}

		protected abstract IAEADCrypto CreateCrypto(ReadOnlySpan<byte> key);

		private void CipherEncrypt(IAEADCrypto crypto, ReadOnlySpan<byte> source, Span<byte> destination, ref int processLength, ref int outLength)
		{
			crypto.Encrypt(NonceSpan, source, destination.Slice(0, source.Length), destination.Slice(source.Length, TagLength));
			processLength += source.Length;
			outLength += source.Length + TagLength;
		}

		private void CipherDecrypt(IAEADCrypto crypto, ReadOnlySpan<byte> source, Span<byte> destination, ref int processLength, ref int outLength)
		{
			var realLength = source.Length - TagLength;
			crypto.Decrypt(NonceSpan, source.Slice(0, realLength), source.Slice(realLength), destination.Slice(0, realLength));
			processLength += source.Length;
			outLength += realLength;
		}

		private void EncryptChunk(ReadOnlySpan<byte> source, Span<byte> destination, ref int processLength, ref int outLength)
		{
			if (source.Length > PayloadLengthLimit)
			{
				throw new Exception(@"Encrypt data is too big");
			}

			Span<byte> payloadLengthBuffer = stackalloc byte[PayloadLengthBytes];
			BinaryPrimitives.WriteUInt16BigEndian(payloadLengthBuffer, (ushort)source.Length);

			var nonce = NonceSpan;

			var unused = 0;
			CipherEncrypt(_crypto!, payloadLengthBuffer, destination, ref unused, ref outLength);
			nonce.Increment();

			CipherEncrypt(_crypto!, source, destination.Slice(PayloadLengthBytes + TagLength), ref processLength, ref outLength);
			nonce.Increment();
		}

		private void DecryptChunk(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, ref int outLength)
		{
			Span<byte> payloadLengthBuffer = stackalloc byte[PayloadLengthBytes];
			processLength = 0;
			var unused = 0;
			CipherDecrypt(_crypto!, source.Slice(0, PayloadLengthBytes + TagLength), payloadLengthBuffer, ref processLength, ref unused);
			var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(payloadLengthBuffer);

			if (payloadLength > PayloadLengthLimit)
			{
				throw new Exception($@"Invalid payloadLength: {payloadLength}");
			}

			if (source.Length < ChunkOverheadSize + payloadLength)
			{
				processLength = 0;
				return;
			}

			var nonce = NonceSpan;
			nonce.Increment();
			CipherDecrypt(_crypto!, source.Slice(processLength, payloadLength + TagLength), destination, ref processLength, ref outLength);
			nonce.Increment();
		}

		public void EncryptTCP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength)
		{
			processLength = 0;
			outLength = 0;

			if (_isFirstPacket)
			{
				if (source.Length < AddressBufferLength)
				{
					return;
				}

				var salt = SaltSpan;
				RandomNumberGenerator.Fill(salt);
				InitCipher();
				salt.CopyTo(destination);
				outLength += SaltLength;

				EncryptChunk(source.Slice(0, AddressBufferLength), destination.Slice(outLength), ref processLength, ref outLength);

				_isFirstPacket = false;
			}

			while (processLength < source.Length)
			{
				var remain = source.Slice(processLength);
				var chunkLength = Math.Min(PayloadLengthLimit, remain.Length);

				EncryptChunk(remain.Slice(0, chunkLength), destination.Slice(outLength), ref processLength, ref outLength);

				if (outLength + ChunkOverheadSize > BufferSize)
				{
					return;
				}
			}
		}

		public void DecryptTCP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength)
		{
			processLength = 0;
			outLength = 0;

			if (_isFirstPacket)
			{
				if (source.Length <= SaltLength)
				{
					return;
				}

				source.Slice(0, SaltLength).CopyTo(SaltSpan);
				InitCipher();
				processLength += SaltLength;

				_isFirstPacket = false;
			}

			while (processLength + ChunkOverheadSize < source.Length)
			{
				var remain = source.Slice(processLength);

				DecryptChunk(remain, destination.Slice(outLength), out var pLen, ref outLength);
				if (pLen == 0)
				{
					return;
				}

				processLength += pLen;

				if (outLength + 100 > BufferSize)
				{
					return;
				}
			}
		}

		public void EncryptUDP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength)
		{
			processLength = 0;

			var salt = SaltSpan;
			RandomNumberGenerator.Fill(salt);
			using var crypto = CreateSessionCrypto();
			salt.CopyTo(destination);
			outLength = SaltLength;

			CipherEncrypt(crypto, source, destination.Slice(SaltLength), ref processLength, ref outLength);
		}

		public void DecryptUDP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength)
		{
			outLength = 0;

			source.Slice(0, SaltLength).CopyTo(SaltSpan);
			using var crypto = CreateSessionCrypto();
			processLength = SaltLength;

			CipherDecrypt(crypto, source.Slice(SaltLength), destination, ref processLength, ref outLength);
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
