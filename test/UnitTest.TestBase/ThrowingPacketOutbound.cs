using Proxy.Abstractions;

namespace UnitTest.TestBase;

public sealed class ThrowingPacketOutbound : IPacketOutbound
{
	public ValueTask<IPacketConnection> CreatePacketConnectionAsync(CancellationToken cancellationToken = default)
	{
		throw new InvalidOperationException("Simulated UDP ASSOCIATE setup failure.");
	}
}
