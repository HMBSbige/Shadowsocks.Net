using Socks5.Enums;
using System;
using System.Net;

namespace Socks5.Models
{
	public struct Socks5UdpReceivePacket
	{
		public Memory<byte> Data;
		public byte Fragment;
		public AddressType Type;
		public IPAddress? Address;
		public string? Domain;
		public ushort Port;
	}
}
