using CryptoBase.Abstractions.SymmetricCryptos;
using CryptoBase.SymmetricCryptos.StreamCryptos;

namespace Shadowsocks.Crypto.Stream;

public class Aes128CfbShadowsocksCrypto : StreamShadowsocksCrypto
{
	public override int KeyLength => 16;

	public override int IvLength => 16;

	public Aes128CfbShadowsocksCrypto(string password) : base(password)
	{
	}

	protected override IStreamCrypto CreateCrypto(bool isEncrypt, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
	{
		return StreamCryptoCreate.AesCfb(isEncrypt, key, iv);
	}
}
