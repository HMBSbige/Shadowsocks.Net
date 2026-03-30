using System.IO.Pipelines;

namespace Proxy.Abstractions;

/// <summary>
/// Stream-oriented inbound capability.
/// </summary>
public interface IStreamInbound : IInbound
{
	/// <summary>
	/// Reads a request from <paramref name="clientPipe"/>, connects to the target via
	/// <paramref name="outbound"/>, and relays the traffic.
	/// </summary>
	ValueTask HandleAsync(InboundContext context, IDuplexPipe clientPipe, IOutbound outbound, CancellationToken cancellationToken = default);
}
