using CryptoBase.Abstractions.SymmetricCryptos;
using System;
using System.Buffers;
using System.Security.Cryptography;

// ReSharper disable VirtualMemberCallInConstructor
namespace Shadowsocks.Crypto.Stream
{
	public abstract class StreamShadowsocksCrypto : IStreamShadowsocksCrypto
	{
		public int AddressBufferLength { get; set; } = -1;

		public byte[] Key { get; }

		public abstract int KeyLength { get; }

		protected Span<byte> KeySpan => Key.AsSpan(0, KeyLength);

		public byte[] Iv { get; }

		public abstract int IvLength { get; }

		protected Span<byte> IvSpan => Iv.AsSpan(0, IvLength);

		private bool _isFirstPacket;

		protected IStreamCrypto? Crypto;

		protected StreamShadowsocksCrypto(string password)
		{
			Reset();

			Key = ArrayPool<byte>.Shared.Rent(KeyLength);
			Iv = ArrayPool<byte>.Shared.Rent(IvLength);
			KeySpan.SsDeriveKey(password);
		}

		public void SetIv(ReadOnlySpan<byte> iv)
		{
			if (iv.Length != IvLength)
			{
				throw new ArgumentException($@"Iv length must be {IvLength}", nameof(iv));
			}

			iv.CopyTo(IvSpan);
			_isFirstPacket = false;
			InitCipher(true);
		}

		protected virtual void InitCipher(bool isEncrypt)
		{
			Crypto?.Dispose();
			Crypto = CreateCrypto(isEncrypt, KeySpan, IvSpan);
		}

		protected abstract IStreamCrypto CreateCrypto(bool isEncrypt, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv);

		protected virtual void UpdateStream(IStreamCrypto crypto, ReadOnlySpan<byte> source, Span<byte> destination)
		{
			crypto.Update(source, destination);
		}

		public void EncryptTCP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength)
		{
			processLength = 0;
			outLength = 0;

			if (_isFirstPacket)
			{
				var iv = IvSpan;
				RandomNumberGenerator.Fill(iv);
				InitCipher(true);
				iv.CopyTo(destination);
				outLength += IvLength;
				_isFirstPacket = false;
			}

			UpdateStream(Crypto!, source, destination.Slice(outLength));
			processLength += source.Length;
			outLength += source.Length;
		}

		public void DecryptTCP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength)
		{
			processLength = 0;
			outLength = 0;

			if (_isFirstPacket)
			{
				if (source.Length < IvLength)
				{
					return;
				}

				source.Slice(0, IvLength).CopyTo(IvSpan);
				InitCipher(false);
				processLength += IvLength;
				_isFirstPacket = false;
			}

			var remain = source.Slice(processLength);
			UpdateStream(Crypto!, remain, destination);
			processLength += remain.Length;
			outLength += remain.Length;
		}

		public void EncryptUDP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength)
		{
			var iv = IvSpan;
			RandomNumberGenerator.Fill(iv);
			using var crypto = CreateCrypto(true, KeySpan, iv);
			iv.CopyTo(destination);
			outLength = IvLength;

			UpdateStream(crypto, source, destination.Slice(IvLength));
			processLength = source.Length;
			outLength += source.Length;
		}

		public void DecryptUDP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength)
		{
			var iv = IvSpan;
			source.Slice(0, IvLength).CopyTo(iv);
			using var crypto = CreateCrypto(false, KeySpan, iv);
			processLength = IvLength;

			var remain = source.Slice(IvLength);
			UpdateStream(crypto, remain, destination);
			processLength += remain.Length;
			outLength = remain.Length;
		}

		public virtual void Dispose()
		{
			ArrayPool<byte>.Shared.Return(Key);
			ArrayPool<byte>.Shared.Return(Iv);

			Crypto?.Dispose();
		}

		public void Reset()
		{
			_isFirstPacket = true;
		}
	}
}
