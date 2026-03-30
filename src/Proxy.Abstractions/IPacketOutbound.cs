namespace Proxy.Abstractions;

/// <summary>
/// Packet-oriented outbound capability.
/// </summary>
public interface IPacketOutbound : IOutbound
{
	/// <summary>
	/// Creates a packet connection. Per-message destinations are specified via
	/// <see cref="IPacketConnection.SendToAsync"/>.
	/// </summary>
	ValueTask<IPacketConnection> CreatePacketConnectionAsync(CancellationToken cancellationToken = default);
}
