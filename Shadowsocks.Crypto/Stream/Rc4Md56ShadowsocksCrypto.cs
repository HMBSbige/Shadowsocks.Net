namespace Shadowsocks.Crypto.Stream
{
	public class Rc4Md56ShadowsocksCrypto : Rc4Md5ShadowsocksCrypto
	{
		public override int IvLength => 6;

		public Rc4Md56ShadowsocksCrypto(string password) : base(password)
		{
		}
	}
}
