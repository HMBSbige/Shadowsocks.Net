using System.IO.Pipelines;

namespace Proxy.Abstractions;

/// <summary>
/// Handles an inbound client connection at the connection level:
/// parses the protocol, determines the target, and forwards traffic via the outbound.
/// </summary>
public interface IInbound
{
	/// <summary>
	/// Reads a request from <paramref name="clientPipe"/>, connects to the target via
	/// <paramref name="outbound"/>, and relays the traffic.
	/// </summary>
	ValueTask HandleAsync(InboundContext context, IDuplexPipe clientPipe, IOutbound outbound, CancellationToken cancellationToken = default);
}
