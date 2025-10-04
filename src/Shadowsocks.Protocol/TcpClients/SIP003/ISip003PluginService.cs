using Shadowsocks.Protocol.Models;
using System.Net;

namespace Shadowsocks.Protocol.TcpClients.SIP003;

public interface ISip003PluginService
{
	IPEndPoint GetPluginService(ShadowsocksServerInfo serverInfo);
	void RemoveService(ShadowsocksServerInfo serverInfo);
	void RemoveAll();
}
