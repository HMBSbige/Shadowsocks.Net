using Microsoft.Extensions.Logging;
using System.Net;

namespace Socks5;

/// <summary>
/// Options used when instantiating a <see cref="Socks5Inbound"/> handler.
/// </summary>
public record Socks5InboundOption
{
	/// <summary>
	/// Optional username/password credential enforced by the inbound handler.
	/// </summary>
	public UserPassAuth? UserPassAuth { get; init; }

	/// <summary>
	/// Optional logger for connection diagnostics.
	/// </summary>
	public ILogger<Socks5Inbound>? Logger { get; init; }

	/// <summary>
	/// Optional address that the UDP relay socket binds to for UDP ASSOCIATE requests.
	/// Defaults to <see cref="IPAddress.Any"/>.
	/// </summary>
	public IPAddress? UdpRelayBindAddress { get; init; }
}
