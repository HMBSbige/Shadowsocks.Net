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

		public byte[] Iv { get; }

		public abstract int IvLength { get; }

		private bool _isFirstPacket;

		protected StreamShadowsocksCrypto(string password)
		{
			Reset();

			Key = ArrayPool<byte>.Shared.Rent(KeyLength);
			Iv = ArrayPool<byte>.Shared.Rent(IvLength);
			Key.AsSpan(0, KeyLength).SsDeriveKey(password);
		}

		public void SetIv(ReadOnlySpan<byte> iv)
		{
			if (iv.Length != IvLength)
			{
				throw new ArgumentException($@"Iv length must be {IvLength}", nameof(iv));
			}

			iv.CopyTo(Iv);
			_isFirstPacket = false;
			InitCipher(true);
		}

		protected abstract void InitCipher(bool isEncrypt);

		protected abstract void UpdateStream(ReadOnlySpan<byte> source, Span<byte> destination);

		public void EncryptTCP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength)
		{
			processLength = 0;
			outLength = 0;

			if (_isFirstPacket)
			{
				RandomNumberGenerator.Fill(Iv);
				InitCipher(true);
				Iv.AsSpan(0, IvLength).CopyTo(destination);
				outLength += IvLength;
				_isFirstPacket = false;
			}

			UpdateStream(source, destination.Slice(outLength));
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

				source.Slice(0, IvLength).CopyTo(Iv);
				InitCipher(false);
				processLength += IvLength;
				_isFirstPacket = false;
			}

			var remain = source.Slice(processLength);
			UpdateStream(remain, destination);
			processLength += remain.Length;
			outLength += remain.Length;
		}

		public void EncryptUDP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength)
		{
			processLength = 0;
			outLength = 0;

			RandomNumberGenerator.Fill(Iv);
			InitCipher(true);
			Iv.AsSpan(0, IvLength).CopyTo(destination);
			outLength += IvLength;

			UpdateStream(source, destination.Slice(outLength));
			processLength += source.Length;
			outLength += source.Length;
		}

		public void DecryptUDP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength)
		{
			processLength = 0;
			outLength = 0;

			source.Slice(0, IvLength).CopyTo(Iv);
			InitCipher(false);
			processLength += IvLength;

			var remain = source.Slice(processLength);
			UpdateStream(remain, destination);
			processLength += remain.Length;
			outLength += remain.Length;
		}

		public virtual void Dispose()
		{
			ArrayPool<byte>.Shared.Return(Key);
			ArrayPool<byte>.Shared.Return(Iv);
		}

		public void Reset()
		{
			_isFirstPacket = true;
		}
	}
}
