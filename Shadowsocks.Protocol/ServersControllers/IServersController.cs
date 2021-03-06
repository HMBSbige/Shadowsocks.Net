using Shadowsocks.Protocol.TcpClients;
using Shadowsocks.Protocol.UdpClients;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.ServersControllers
{
	public interface IServersController
	{
		ValueTask<IPipeClient> GetServerAsync(string target);
		ValueTask<IUdpClient> GetServerUdpAsync(string target);
	}
}
