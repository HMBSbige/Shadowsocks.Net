namespace Proxy.Abstractions;

/// <summary>
/// Packet-oriented inbound capability.
/// </summary>
public interface IPacketInbound : IInbound
{
	/// <summary>
	/// Reads packets from <paramref name="clientPackets"/>, processes them via
	/// <paramref name="outbound"/>, and relays the traffic.
	/// </summary>
	ValueTask HandleAsync(InboundContext context, IPacketConnection clientPackets, IOutbound outbound, CancellationToken cancellationToken = default);
}
