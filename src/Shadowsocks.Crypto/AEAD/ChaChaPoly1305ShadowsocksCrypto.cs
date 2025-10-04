using CryptoBase.Abstractions.SymmetricCryptos;
using CryptoBase.SymmetricCryptos.AEADCryptos;

namespace Shadowsocks.Crypto.AEAD;

public class ChaChaPoly1305ShadowsocksCrypto : AEADShadowsocksCrypto
{
	public override int KeyLength => 32;

	public override int SaltLength => 32;

	public ChaChaPoly1305ShadowsocksCrypto(string password) : base(password)
	{
	}

	protected override IAEADCrypto CreateCrypto(ReadOnlySpan<byte> key)
	{
		return AEADCryptoCreate.ChaCha20Poly1305(key);
	}
}
