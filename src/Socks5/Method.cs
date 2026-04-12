namespace Socks5;

/// <summary>
/// Enumerates the authentication methods supported during the SOCKS5 handshake (RFC 1928).
/// </summary>
/// <remarks>
/// The server selects one of these values to confirm how the client must authenticate.
/// </remarks>
public enum Method : byte
{
	/// <summary>No authentication is required.</summary>
	NoAuthentication = 0x00,
	/// <summary>GSSAPI-based authentication (rarely used in practice).</summary>
	GSSAPI = 0x01,
	/// <summary>Username/password authentication as defined in RFC 1929.</summary>
	UsernamePassword = 0x02,
	/// <summary>No acceptable authentication method was offered.</summary>
	NoAcceptable = 0xff
}
