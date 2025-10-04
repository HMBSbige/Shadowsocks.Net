namespace Shadowsocks.Crypto.Stream;

public class Aes192CtrShadowsocksCrypto : Aes128CtrShadowsocksCrypto
{
	public override int KeyLength => 24;

	public Aes192CtrShadowsocksCrypto(string password) : base(password)
	{
	}
}
