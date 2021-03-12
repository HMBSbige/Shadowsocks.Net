namespace Shadowsocks.Crypto.AEAD
{
	public class Aes192GcmShadowsocksCrypto : Aes128GcmShadowsocksCrypto
	{
		public override int KeyLength => 24;

		public override int SaltLength => 24;

		public Aes192GcmShadowsocksCrypto(string password) : base(password)
		{
		}
	}
}
