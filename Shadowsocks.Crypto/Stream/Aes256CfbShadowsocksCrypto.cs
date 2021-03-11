namespace Shadowsocks.Crypto.Stream
{
	public class Aes256CfbShadowsocksCrypto : Aes128CfbShadowsocksCrypto
	{
		public override int KeyLength => 32;

		public Aes256CfbShadowsocksCrypto(string password) : base(password)
		{
		}
	}
}
