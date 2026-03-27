namespace Socks5;

internal enum Command : byte
{
	Connect = 0x01,
	Bind = 0x02,
	UdpAssociate = 0x03
}
