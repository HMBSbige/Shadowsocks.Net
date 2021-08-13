using Socks5.Enums;
using System;
using System.Net;

namespace Socks5.Models
{
	// +----+------+------+----------+----------+----------+
	// |RSV | FRAG | ATYP | DST.ADDR | DST.PORT |   DATA   |
	// +----+------+------+----------+----------+----------+
	// | 2  |  1   |  1   | Variable |    2     | Variable |
	// +----+------+------+----------+----------+----------+
	public struct Socks5UdpReceivePacket
	{
		public byte Fragment;
		public AddressType Type;
		public IPAddress? Address;
		public string? Domain;
		public ushort Port;
		public ReadOnlyMemory<byte> Data;
	}
}
