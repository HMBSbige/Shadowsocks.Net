namespace Shadowsocks.Crypto.Stream
{
	public class Aes192CfbShadowsocksCrypto : Aes128CfbShadowsocksCrypto
	{
		public override int KeyLength => 24;

		public Aes192CfbShadowsocksCrypto(string password) : base(password)
		{
		}
	}
}
