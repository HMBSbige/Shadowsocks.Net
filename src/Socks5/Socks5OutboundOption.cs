using System.Net;

namespace Socks5;

/// <summary>
/// Options used when instantiating a <see cref="Socks5Outbound"/> client.
/// </summary>
public record Socks5OutboundOption
{
	/// <summary>
	/// The SOCKS5 server IP address.
	/// </summary>
	public required IPAddress Address { get; init; }

	/// <summary>
	/// The SOCKS5 server port.
	/// </summary>
	public required ushort Port { get; init; }

	/// <summary>
	/// Optional username/password credential for the SOCKS5 server (RFC 1929).
	/// </summary>
	public UserPassAuth? UserPassAuth { get; init; }
}
