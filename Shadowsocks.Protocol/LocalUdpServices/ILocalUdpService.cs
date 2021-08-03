using System.Net.Sockets;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.LocalUdpServices
{
	public interface ILocalUdpService
	{
		ValueTask<bool> IsHandleAsync(UdpReceiveResult receiveResult, UdpClient incoming);
		void Stop();
	}
}
