namespace Shadowsocks.Crypto.Stream
{
	public class Aes256CtrShadowsocksCrypto : Aes128CtrShadowsocksCrypto
	{
		public override int KeyLength => 32;

		public Aes256CtrShadowsocksCrypto(string password) : base(password)
		{
		}
	}
}
