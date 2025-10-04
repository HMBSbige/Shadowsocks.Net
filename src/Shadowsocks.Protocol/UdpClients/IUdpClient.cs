namespace Shadowsocks.Protocol.UdpClients;

public interface IUdpClient : IDisposable
{
	ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
	ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
}
