using CryptoBase.Abstractions.Digests;
using CryptoBase.Abstractions.SymmetricCryptos;
using CryptoBase.SymmetricCryptos.StreamCryptos;
using System.Buffers;

namespace Shadowsocks.Crypto.Stream;

public class Rc4Md5ShadowsocksCrypto : StreamShadowsocksCrypto
{
	public override int KeyLength => 16;

	public override int IvLength => 16;

	public Rc4Md5ShadowsocksCrypto(string password) : base(password)
	{
	}

	protected override IStreamCrypto CreateCrypto(bool isEncrypt, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
	{
		byte[] buffer = ArrayPool<byte>.Shared.Rent(KeyLength + IvLength + HashConstants.Md5Length);
		try
		{
			Span<byte> realKey = buffer.AsSpan(0, HashConstants.Md5Length);
			Span<byte> temp = buffer.AsSpan(HashConstants.Md5Length, KeyLength + IvLength);

			key.CopyTo(temp);
			iv.CopyTo(temp[KeyLength..]);
			temp.ToMd5(realKey);

			return StreamCryptoCreate.Rc4(realKey);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}
}
