namespace Proxy.Abstractions;

/// <summary>
/// Packet-oriented outbound capability.
/// </summary>
public interface IPacketOutbound : IOutbound
{
	/// <summary>
	/// Creates a packet connection associated with the specified <paramref name="destination"/>.
	/// </summary>
	ValueTask<IPacketConnection> CreatePacketConnectionAsync(ProxyDestination destination, CancellationToken cancellationToken = default);
}
