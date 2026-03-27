namespace Socks5;

// +----+------+------+----------+----------+----------+
// |RSV | FRAG | ATYP | DST.ADDR | DST.PORT |   DATA   |
// +----+------+------+----------+----------+----------+
// | 2  |  1   |  1   | Variable |    2     | Variable |
// +----+------+------+----------+----------+----------+
internal struct Socks5UdpReceivePacket
{
	public byte Fragment;
	public AddressType Type;
	public HostField Host;
	public ushort Port;
	public ReadOnlyMemory<byte> Data;
}
