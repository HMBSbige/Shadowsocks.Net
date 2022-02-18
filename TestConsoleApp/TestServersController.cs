using Shadowsocks.Crypto;
using Shadowsocks.Protocol.Models;
using Shadowsocks.Protocol.ServersControllers;
using Shadowsocks.Protocol.TcpClients;
using Shadowsocks.Protocol.TcpClients.SIP003;
using Shadowsocks.Protocol.UdpClients;

namespace TestConsoleApp;

public class TestServersController : IServersController
{
	private static readonly Dictionary<ShadowsocksServerInfo, ISip003PluginService> PluginServices = new();

	private static ShadowsocksServerInfo GetInfo()
	{
		return new ShadowsocksServerInfo
		{
			Address = @"",
			Port = 0,
			Method = ShadowsocksCrypto.Aes128GcmMethod,
			Password = @"",
			Plugin = @"",
			PluginOpts = @""
		};
	}

	public async ValueTask<IPipeClient> GetServerAsync(string target)
	{
		ShadowsocksServerInfo info = GetInfo();

		if (!string.IsNullOrEmpty(info.Plugin))
		{
			ISip003PluginService? service;
			lock (PluginServices)
			{
				if (!PluginServices.TryGetValue(info, out service))
				{
					service = OperatingSystem.IsWindows() ? new Sip003PluginWindowsService() : new Sip003PluginService();
					service.Start(info);
					PluginServices.Add(info, service);
				}
			}

			info = info with { Address = service.LocalEndPoint!.Address.ToString(), Port = (ushort)service.LocalEndPoint.Port };
		}

		IPipeClient client = new ShadowsocksTcpClient(info);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));

		await client.ConnectAsync(cts.Token);

		return client;
	}

	public ValueTask<IUdpClient> GetServerUdpAsync(string target)
	{
		ShadowsocksServerInfo info = GetInfo();

		IUdpClient client = new ShadowsocksUdpClient(info);

		return ValueTask.FromResult(client);
	}
}
