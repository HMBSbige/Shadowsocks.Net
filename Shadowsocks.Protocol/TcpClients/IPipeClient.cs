using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.TcpClients
{
	public interface IPipeClient : IAsyncDisposable
	{
		ValueTask ConnectAsync(CancellationToken cancellationToken = default);

		IDuplexPipe GetPipe(string targetAddress, ushort targetPort);
	}
}
