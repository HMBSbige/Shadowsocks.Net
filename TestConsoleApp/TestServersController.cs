using Shadowsocks.Crypto;
using Shadowsocks.Protocol.Models;
using Shadowsocks.Protocol.ServersControllers;
using Shadowsocks.Protocol.TcpClients;
using Shadowsocks.Protocol.UdpClients;

namespace TestConsoleApp;

public class TestServersController : IServersController
{
	public async ValueTask<IPipeClient> GetServerAsync(string target)
	{
		ShadowsocksServerInfo info = new()
		{
			Address = @"",
			Port = 0,
			Method = ShadowsocksCrypto.Aes128GcmMethod,
			Password = @"",
			Remark = @""
		};

		IPipeClient client = new ShadowsocksTcpClient(info);

		CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));

		await client.ConnectAsync(cts.Token);

		return client;
	}

	public ValueTask<IUdpClient> GetServerUdpAsync(string target)
	{
		ShadowsocksServerInfo info = new()
		{
			Address = @"",
			Port = 0,
			Method = ShadowsocksCrypto.Aes128GcmMethod,
			Password = @"",
			Remark = @""
		};

		IUdpClient client = new ShadowsocksUdpClient(info);

		return ValueTask.FromResult(client);
	}
}
