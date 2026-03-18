namespace Proxy.Abstractions;

/// <summary>
/// Creates outbound connections to a target destination.
/// </summary>
public interface IOutbound
{
	/// <summary>
	/// Connects to the specified <paramref name="destination"/>.
	/// </summary>
	ValueTask<IConnection> ConnectAsync(ProxyDestination destination, CancellationToken cancellationToken = default);
}
