using Shadowsocks.Crypto;
using Shadowsocks.Protocol.Models;

namespace UnitTest;

public class SIP002Test
{
	[Test]
	[Arguments(@"ss://YWVzLTEyOC1nY206dGVzdA@192.168.100.1:8888/#Example1",
		@"Example1",
		@"192.168.100.1",
		(ushort)8888,
		@"test",
		ShadowsocksCrypto.Aes128GcmMethod,
		default,
		default
	)]
	[Arguments(@"ss://cmM0LW1kNTpwYXNzd2Q@192.168.100.1:8888/?plugin=obfs-local%3bobfs%3dhttp#Example2",
		@"Example2",
		@"192.168.100.1",
		(ushort)8888,
		@"passwd",
		ShadowsocksCrypto.Rc4Md5Method,
		@"obfs-local",
		@"obfs=http"
	)]
	[Arguments(@"ss://Y2hhY2hhMjAtaWV0Zi1wb2x5MTMwNTpzYWR4czslMjA4NDhzMTUxMg@localhost.local:114/?plugin=simple-obfs%3bobfs%3dhttp%3bobfs-host%3dexample.com#%e7%a9%ba%e6%a0%bc%20%e6%b5%8b%e8%af%95",
		@"空格 测试",
		@"localhost.local",
		(ushort)114,
		@"sadxs;%20848s1512",
		ShadowsocksCrypto.ChaCha20IetfPoly1305Method,
		@"simple-obfs",
		@"obfs=http;obfs-host=example.com"
	)]
	public async Task Sip002UriSchemeTest(string uri,
		string? remark,
		string? address, ushort port,
		string? password, string? method,
		string? plugin, string? pluginOpts)
	{
		ShadowsocksServerInfo serverInfo = new()
		{
			Remarks = remark,
			Address = address,
			Port = port,
			Password = password,
			Method = method,
			Plugin = plugin,
			PluginOpts = pluginOpts
		};
		await Assert.That(serverInfo.ToSip002UriSchemeString()).IsEqualTo(uri);
		await Assert.That(ShadowsocksServerInfo.TryParse(uri, out ShadowsocksServerInfo? newInfo)).IsTrue();
		await Assert.That(newInfo).IsNotNull();
		newInfo!.Id = serverInfo.Id;
		await Assert.That(newInfo).IsEqualTo(serverInfo);
	}
}
