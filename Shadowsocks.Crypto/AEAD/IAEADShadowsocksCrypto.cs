namespace Shadowsocks.Crypto.AEAD
{
	public interface IAEADShadowsocksCrypto : IShadowsocksCrypto
	{
		byte[] SessionKey { get; }

		byte[] Nonce { get; }

		int NonceLength { get; }

		byte[] Salt { get; }

		int SaltLength { get; }
	}
}
