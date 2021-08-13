using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.LocalUdpServices
{
	public interface ILocalUdpService
	{
		bool IsHandle(ReadOnlyMemory<byte> buffer);

		ValueTask HandleAsync(UdpReceiveResult receiveResult, UdpClient incoming, CancellationToken cancellationToken = default);
	}
}
