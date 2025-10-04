using System.IO.Pipelines;

namespace Shadowsocks.Protocol.TcpClients;

public interface IPipeClient : IDisposable
{
	ValueTask ConnectAsync(CancellationToken cancellationToken = default);

	IDuplexPipe GetPipe();
}
