namespace Socks5;

/// <summary>
/// Represents the reply status codes defined by the SOCKS5 protocol (RFC 1928).
/// </summary>
public enum Socks5Reply : byte
{
	/// <summary>The request completed successfully.</summary>
	Succeeded = 0x00,
	/// <summary>A generic SOCKS server failure occurred.</summary>
	GeneralFailure = 0x01,
	/// <summary>The client is not permitted to connect by the configured ruleset.</summary>
	ConnectionNotAllowed = 0x02,
	/// <summary>The destination network could not be reached.</summary>
	NetworkUnreachable = 0x03,
	/// <summary>The destination host could not be reached.</summary>
	HostUnreachable = 0x04,
	/// <summary>The destination host actively refused the connection.</summary>
	ConnectionRefused = 0x05,
	/// <summary>The request failed because its TTL expired.</summary>
	TtlExpired = 0x06,
	/// <summary>The requested command is not supported.</summary>
	CommandNotSupported = 0x07,
	/// <summary>The requested address type is not supported.</summary>
	AddressTypeNotSupported = 0x08
}
