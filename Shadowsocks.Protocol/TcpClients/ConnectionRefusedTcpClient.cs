using Microsoft;
using System.IO.Pipelines;

namespace Shadowsocks.Protocol.TcpClients;

public class ConnectionRefusedTcpClient : IPipeClient
{
	public static readonly ConnectionRefusedTcpClient Default = new Lazy<ConnectionRefusedTcpClient>().Value;

	public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
	{
		throw Assumes.NotReachable();
	}

	public IDuplexPipe GetPipe(string targetAddress, ushort targetPort)
	{
		throw Assumes.NotReachable();
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);
	}
}
