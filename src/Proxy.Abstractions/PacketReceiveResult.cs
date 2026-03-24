namespace Proxy.Abstractions;

/// <summary>
/// Result of a packet receive operation, including the remote endpoint.
/// </summary>
public readonly struct PacketReceiveResult
{
	/// <summary>Number of bytes received.</summary>
	public int BytesReceived { get; init; }

	/// <summary>The remote destination from which the datagram was received.</summary>
	public ProxyDestination RemoteDestination { get; init; }
}
