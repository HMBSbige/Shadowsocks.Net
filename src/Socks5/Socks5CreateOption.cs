using System.Net;

namespace Socks5;

/// <summary>
/// Options used when instantiating a <see cref="Socks5Outbound"/> client.
/// </summary>
/// <remarks>
/// <see cref="Address"/> and <see cref="Port"/> define the SOCKS5 server endpoint,
/// while <see cref="UserPassAuth"/> holds optional credentials for username/password authentication.
/// </remarks>
public record Socks5CreateOption
{
	/// <summary>
	/// The SOCKS5 server IP address to connect to.
	/// </summary>
	public IPAddress? Address { get; set; }

	/// <summary>
	/// The port of the SOCKS5 server.
	/// </summary>
	public ushort Port { get; set; }

	/// <summary>
	/// The optional username/password credential for the SOCKS5 server (RFC 1929).
	/// </summary>
	public UserPassAuth? UserPassAuth { get; set; }
}
