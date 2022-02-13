using CryptoBase.Abstractions.SymmetricCryptos;
using CryptoBase.SymmetricCryptos.StreamCryptos;

namespace Shadowsocks.Crypto.Stream;

public class ChaCha20IETFShadowsocksCrypto : StreamShadowsocksCrypto
{
	public override int KeyLength => 32;

	public override int IvLength => 12;

	public ChaCha20IETFShadowsocksCrypto(string password) : base(password)
	{
	}

	protected override IStreamCrypto CreateCrypto(bool isEncrypt, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
	{
		return StreamCryptoCreate.ChaCha20(key, iv);
	}
}
