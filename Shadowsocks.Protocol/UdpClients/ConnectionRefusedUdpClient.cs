using Microsoft;

namespace Shadowsocks.Protocol.UdpClients;

public class ConnectionRefusedUdpClient : IUdpClient
{
	public static readonly ConnectionRefusedUdpClient Default = new Lazy<ConnectionRefusedUdpClient>().Value;

	public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		throw Assumes.NotReachable();
	}

	public ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
	{
		throw Assumes.NotReachable();
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);
	}
}
