using Shadowsocks.Protocol.Models;
using System.Net;

namespace Shadowsocks.Protocol.TcpClients.SIP003;

public class Sip003PluginService : ISip003PluginService
{
	private readonly Dictionary<ShadowsocksServerInfo, ISip003Plugin> _services = new();

	public IPEndPoint GetPluginService(ShadowsocksServerInfo serverInfo)
	{
		lock (_services)
		{
			if (_services.TryGetValue(serverInfo, out ISip003Plugin? plugin))
			{
				if (plugin.LocalEndPoint is not null)
				{
					return plugin.LocalEndPoint;
				}
				plugin.Dispose();
			}

			plugin = OperatingSystem.IsWindows() ? new Sip003PluginWindows() : new Sip003Plugin();
			plugin.Start(serverInfo);
			_services[serverInfo] = plugin;
			return plugin.LocalEndPoint;
		}
	}

	public void RemoveService(ShadowsocksServerInfo serverInfo)
	{
		lock (_services)
		{
			if (_services.Remove(serverInfo, out ISip003Plugin? plugin))
			{
				plugin.Dispose();
			}
		}
	}

	public void RemoveAll()
	{
		lock (_services)
		{
			foreach ((ShadowsocksServerInfo _, ISip003Plugin plugin) in _services)
			{
				plugin.Dispose();
			}
			_services.Clear();
		}
	}
}
