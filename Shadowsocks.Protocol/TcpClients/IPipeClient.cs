using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.TcpClients
{
	public interface IPipeClient : IAsyncDisposable
	{
		IDuplexPipe? Pipe { get; }

		ValueTask<bool> TryConnectAsync(CancellationToken token);
	}
}
