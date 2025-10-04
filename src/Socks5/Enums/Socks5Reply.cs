namespace Socks5.Enums;

public enum Socks5Reply : byte
{
	Succeeded = 0x00,
	GeneralFailure = 0x01,
	ConnectionNotAllowed = 0x02,
	NetworkUnreachable = 0x03,
	HostUnreachable = 0x04,
	ConnectionRefused = 0x05,
	TtlExpired = 0x06,
	CommandNotSupported = 0x07,
	AddressTypeNotSupported = 0x08
}
