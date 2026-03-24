namespace Proxy.Abstractions;

/// <summary>
/// Stream-oriented outbound capability.
/// </summary>
public interface IStreamOutbound : IOutbound
{
	/// <summary>
	/// Connects to the specified <paramref name="destination"/>.
	/// </summary>
	ValueTask<IConnection> ConnectAsync(ProxyDestination destination, CancellationToken cancellationToken = default);
}
