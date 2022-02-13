using CryptoBase.Abstractions.SymmetricCryptos;
using System.Buffers;
using System.Security.Cryptography;

// ReSharper disable VirtualMemberCallInConstructor
namespace Shadowsocks.Crypto.Stream;

public abstract class StreamShadowsocksCrypto : IStreamShadowsocksCrypto
{
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
			Span<byte> iv = IvSpan;
			RandomNumberGenerator.Fill(iv);
			InitCipher(true);
			iv.CopyTo(destination);
			outLength += IvLength;
			_isFirstPacket = false;
		}

		UpdateStream(Crypto!, source, destination[outLength..]);
		processLength += source.Length;
		outLength += source.Length;
	}

	public int DecryptTCP(ref ReadOnlySequence<byte> source, Span<byte> destination)
	{
		int outLength = 0;

		if (_isFirstPacket)
		{
			if (source.Length < IvLength)
			{
				return 0;
			}

			source.Slice(0, IvLength).CopyTo(IvSpan);
			InitCipher(false);
			source = source.Slice(IvLength);
			_isFirstPacket = false;
		}

		long remainLength = Math.Min(source.Length, destination.Length);
		ReadOnlySequence<byte> remain = source.Slice(0, remainLength);
		source = source.Slice(remainLength);

		foreach (ReadOnlyMemory<byte> memory in remain)
		{
			UpdateStream(Crypto!, memory.Span, destination[outLength..]);
			outLength += memory.Length;
		}

		return outLength;
	}

	public int EncryptUDP(ReadOnlySpan<byte> source, Span<byte> destination)
	{
		Span<byte> iv = IvSpan;
		RandomNumberGenerator.Fill(iv);
		using IStreamCrypto crypto = CreateCrypto(true, KeySpan, iv);
		iv.CopyTo(destination);

		UpdateStream(crypto, source, destination[IvLength..]);

		return IvLength + source.Length;
	}

	public int DecryptUDP(ReadOnlySpan<byte> source, Span<byte> destination)
	{
		Span<byte> iv = IvSpan;
		source[..IvLength].CopyTo(iv);
		using IStreamCrypto crypto = CreateCrypto(false, KeySpan, iv);

		ReadOnlySpan<byte> remain = source[IvLength..];
		UpdateStream(crypto, remain, destination);
		return remain.Length;
	}

	public void Dispose()
	{
		ArrayPool<byte>.Shared.Return(Key);
		ArrayPool<byte>.Shared.Return(Iv);

		Crypto?.Dispose();
		GC.SuppressFinalize(this);
	}

	public void Reset()
	{
		_isFirstPacket = true;
	}
}
