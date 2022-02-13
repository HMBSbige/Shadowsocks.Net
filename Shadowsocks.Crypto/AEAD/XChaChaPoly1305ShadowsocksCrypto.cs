using CryptoBase.Abstractions.SymmetricCryptos;
using CryptoBase.SymmetricCryptos.AEADCryptos;

namespace Shadowsocks.Crypto.AEAD;

public class XChaChaPoly1305ShadowsocksCrypto : AEADShadowsocksCrypto
{
	public override int KeyLength => 32;

	public override int SaltLength => 32;

	public override int NonceLength => 24;

	public XChaChaPoly1305ShadowsocksCrypto(string password) : base(password)
	{
	}

	protected override IAEADCrypto CreateCrypto(ReadOnlySpan<byte> key)
	{
		return AEADCryptoCreate.XChaCha20Poly1305(key);
	}
}
