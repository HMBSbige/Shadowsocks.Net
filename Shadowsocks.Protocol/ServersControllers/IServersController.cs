using Shadowsocks.Protocol.TcpClients;
using Shadowsocks.Protocol.UdpClients;

namespace Shadowsocks.Protocol.ServersControllers;

public interface IServersController
{
	ValueTask<IPipeClient> GetServerAsync(string target, ushort targetPort);
	ValueTask<IUdpClient> GetServerUdpAsync(string target, ushort targetPort);
}
