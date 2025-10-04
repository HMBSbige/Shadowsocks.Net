using CryptoBase.Abstractions.SymmetricCryptos;
using CryptoBase.SymmetricCryptos.AEADCryptos;

namespace Shadowsocks.Crypto.AEAD;

public class Sm4GcmShadowsocksCrypto : AEADShadowsocksCrypto
{
	public override int KeyLength => 16;

	public override int SaltLength => 16;

	public Sm4GcmShadowsocksCrypto(string password) : base(password)
	{
	}

	protected override IAEADCrypto CreateCrypto(ReadOnlySpan<byte> key)
	{
		return AEADCryptoCreate.Sm4Gcm(key);
	}
}
