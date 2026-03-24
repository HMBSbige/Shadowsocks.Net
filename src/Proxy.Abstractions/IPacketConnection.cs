namespace Proxy.Abstractions;

/// <summary>
/// A packet-oriented connection with per-message addressing.
/// </summary>
public interface IPacketConnection : IAsyncDisposable
{
	/// <summary>
	/// Sends <paramref name="data"/> to the specified <paramref name="destination"/>.
	/// </summary>
	ValueTask<int> SendToAsync(ReadOnlyMemory<byte> data, ProxyDestination destination, CancellationToken cancellationToken = default);

	/// <summary>
	/// Receives one packet into <paramref name="buffer"/> and returns the result including the remote destination.
	/// </summary>
	ValueTask<PacketReceiveResult> ReceiveFromAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}
