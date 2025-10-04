namespace Socks5.Enums;

public enum Method : byte
{
	NoAuthentication = 0x00,
	GSSAPI = 0x01,
	UsernamePassword = 0x02,
	NoAcceptable = 0xff
}
