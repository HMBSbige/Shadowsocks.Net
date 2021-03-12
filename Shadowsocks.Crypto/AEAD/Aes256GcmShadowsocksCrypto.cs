namespace Shadowsocks.Crypto.AEAD
{
	public class Aes256GcmShadowsocksCrypto : Aes128GcmShadowsocksCrypto
	{
		public override int KeyLength => 32;

		public override int SaltLength => 32;

		public Aes256GcmShadowsocksCrypto(string password) : base(password)
		{
		}
	}
}
