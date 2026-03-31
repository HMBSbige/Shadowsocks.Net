using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Socks5;

internal struct ServerBound
{
	/// <summary>
	/// An unspecified endpoint (IPv4 0.0.0.0:0) for SOCKS5 replies where the
	/// bound address is not meaningful — e.g. error replies or when the actual
	/// bound address is unavailable (RFC 1928, Section 6).
	/// </summary>
	public static readonly ServerBound Unspecified = CreateUnspecified();

	public AddressType Type;
	public HostField Host;
	public ushort Port;

	internal static ServerBound FromSocketAddress(SocketAddress sa)
	{
		(int off, int len) = Socks5Utils.SockAddrSlice(sa.Family);
		IPAddress addr = new(sa.Buffer.Span.Slice(off, len));

		if (addr.IsIPv4MappedToIPv6)
		{
			addr = addr.MapToIPv4();
		}

		ServerBound b = default;
		b.Type = addr.AddressFamily is AddressFamily.InterNetworkV6 ? AddressType.IPv6 : AddressType.IPv4;
		addr.TryFormat(b.Host.WriteBuffer, out b.Host.Length);
		b.Port = BinaryPrimitives.ReadUInt16BigEndian(sa.Buffer.Span.Slice(2));
		return b;
	}

	private static ServerBound CreateUnspecified()
	{
		ServerBound b = default;
		b.Type = AddressType.IPv4;
		"0.0.0.0"u8.CopyTo(b.Host.WriteBuffer);
		b.Host.Length = 7;
		return b;
	}
}
