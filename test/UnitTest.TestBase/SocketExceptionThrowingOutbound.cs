using Proxy.Abstractions;
using System.Net.Sockets;

namespace UnitTest.TestBase;

public sealed class SocketExceptionThrowingOutbound(SocketError error) : IStreamOutbound, IPacketOutbound
{
	public ValueTask<IConnection> ConnectAsync(ProxyDestination destination, CancellationToken cancellationToken = default)
	{
		throw new SocketException((int)error);
	}

	public ValueTask<IPacketConnection> CreatePacketConnectionAsync(CancellationToken cancellationToken = default)
	{
		throw new SocketException((int)error);
	}
}
