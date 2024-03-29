using System.Net.Sockets;

namespace Shadowsocks.Protocol.LocalUdpServices;

public interface ILocalUdpService
{
	bool IsHandle(ReadOnlyMemory<byte> buffer);

	ValueTask HandleAsync(UdpReceiveResult receiveResult, UdpClient incoming, CancellationToken cancellationToken = default);
}
