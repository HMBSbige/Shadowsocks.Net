using System.Net;

namespace Proxy.Abstractions;

/// <summary>
/// Per-connection metadata supplied by the accept loop.
/// </summary>
public sealed class InboundContext
{
	/// <summary>Client (remote) IP address of the accepted connection.</summary>
	public required IPAddress ClientAddress { get; init; }

	/// <summary>Client (remote) port of the accepted connection.</summary>
	public required ushort ClientPort { get; init; }

	/// <summary>Local IP address on which the connection was accepted.</summary>
	public required IPAddress LocalAddress { get; init; }

	/// <summary>Local port on which the connection was accepted.</summary>
	public required ushort LocalPort { get; init; }
}
