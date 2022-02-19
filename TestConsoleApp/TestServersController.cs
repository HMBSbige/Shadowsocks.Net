using Shadowsocks.Crypto;
using Shadowsocks.Protocol.Models;
using Shadowsocks.Protocol.ServersControllers;
using Shadowsocks.Protocol.TcpClients;
using Shadowsocks.Protocol.TcpClients.SIP003;
using Shadowsocks.Protocol.UdpClients;
using System.Net;

namespace TestConsoleApp;

public class TestServersController : IServersController
{
	private static readonly ISip003PluginService PluginServices = new Sip003PluginService();

	private static readonly ShadowsocksServerInfo TestInfo = new()
	{
		Remarks = default,
		Address = @"",
		Port = 0,
		Method = ShadowsocksCrypto.Aes128GcmMethod,
		Password = @"",
		Plugin = @"",
		PluginOpts = @""
	};

	private static ShadowsocksServerInfo GetInfo()
	{
		return TestInfo;
	}

	public async ValueTask<IPipeClient> GetServerAsync(string target)
	{
		ShadowsocksServerInfo info = GetInfo();

		if (string.IsNullOrEmpty(info.Plugin))
		{
			return await ConnectAsync(info, TimeSpan.FromSeconds(3));
		}

		IPEndPoint pluginIpEndPoint = PluginServices.GetPluginService(info);
		ShadowsocksServerInfo newInfo = info with { Address = pluginIpEndPoint.Address.ToString(), Port = (ushort)pluginIpEndPoint.Port };

		try
		{
			return await ConnectAsync(newInfo, TimeSpan.FromSeconds(1));
		}
		catch
		{
			PluginServices.RemoveService(info);
			pluginIpEndPoint = PluginServices.GetPluginService(info);
			newInfo = info with { Address = pluginIpEndPoint.Address.ToString(), Port = (ushort)pluginIpEndPoint.Port };
			return await ConnectAsync(newInfo, TimeSpan.FromSeconds(1));
		}

		async ValueTask<IPipeClient> ConnectAsync(ShadowsocksServerInfo serverInfo, TimeSpan timeout)
		{
			IPipeClient client = new ShadowsocksTcpClient(serverInfo);
			using CancellationTokenSource cts = new(timeout);
			await client.ConnectAsync(cts.Token);
			return client;
		}
	}

	public ValueTask<IUdpClient> GetServerUdpAsync(string target)
	{
		ShadowsocksServerInfo info = GetInfo();

		IUdpClient client = new ShadowsocksUdpClient(info);

		return ValueTask.FromResult(client);
	}
}
