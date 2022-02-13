using System.IO.Pipelines;

namespace Shadowsocks.Protocol.TcpClients;

public interface IPipeClient : IAsyncDisposable
{
	ValueTask ConnectAsync(CancellationToken cancellationToken = default);

	IDuplexPipe GetPipe(string targetAddress, ushort targetPort);
}
