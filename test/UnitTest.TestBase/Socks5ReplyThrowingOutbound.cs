using Proxy.Abstractions;
using Socks5;

namespace UnitTest.TestBase;

public sealed class Socks5ReplyThrowingOutbound(Socks5Reply reply) : IStreamOutbound, IPacketOutbound
{
	public ValueTask<IConnection> ConnectAsync(ProxyDestination destination, CancellationToken cancellationToken = default)
	{
		throw new Socks5ProtocolErrorException($"Simulated upstream SOCKS5 error: {reply}.", reply);
	}

	public ValueTask<IPacketConnection> CreatePacketConnectionAsync(CancellationToken cancellationToken = default)
	{
		throw new Socks5ProtocolErrorException($"Simulated upstream SOCKS5 error: {reply}.", reply);
	}
}
