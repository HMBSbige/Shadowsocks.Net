using System.Diagnostics;
using System.IO.Pipelines;

namespace Shadowsocks.Protocol.TcpClients;

public class ConnectionRefusedTcpClient : IPipeClient
{
	public static readonly ConnectionRefusedTcpClient Default = new Lazy<ConnectionRefusedTcpClient>().Value;

	public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
	{
		throw new UnreachableException();
	}

	public IDuplexPipe GetPipe()
	{
		throw new UnreachableException();
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);
	}
}
