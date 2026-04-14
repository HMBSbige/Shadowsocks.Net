using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Socks5;

internal struct ServerBound
{
	/// <summary>
	/// An unspecified endpoint (IPv4 0.0.0.0:0) for SOCKS5 replies where the
	/// bound address is not meaningful (error replies) or genuinely unavailable
	/// (e.g. proxy-chained connections with no local socket).
	/// RFC 1928 §6 assumes the bound address is always known and does not
	/// address this edge case; 0.0.0.0:0 is the de-facto industry convention.
	/// </summary>
	public static readonly ServerBound Unspecified = CreateUnspecified();

	public AddressType Type;
	public HostField Host;
	public ushort Port;

	internal static bool TryFromSocketAddress(SocketAddress? socketAddress, out ServerBound bound)
	{
		bound = default;

		if (socketAddress is null)
		{
			return false;
		}

		AddressType type;

		switch (socketAddress.Family)
		{
			case AddressFamily.InterNetwork:
				type = AddressType.IPv4;
				break;
			case AddressFamily.InterNetworkV6:
				type = AddressType.IPv6;
				break;
			default:
				return false;
		}

		(int addressOffset, int addressLength) = Socks5Utils.SockAddrSlice(socketAddress.Family);

		if (socketAddress.Size < addressOffset + addressLength)
		{
			return false;
		}

		IPAddress address = new(socketAddress.Buffer.Span.Slice(addressOffset, addressLength));

		if (address.IsIPv4MappedToIPv6)
		{
			address = address.MapToIPv4();
			type = AddressType.IPv4;
		}

		bound.Type = type;

		if (!address.TryFormat(bound.Host.WriteBuffer, out bound.Host.Length))
		{
			bound = default;
			return false;
		}

		bound.Port = BinaryPrimitives.ReadUInt16BigEndian(socketAddress.Buffer.Span.Slice(2));
		return true;
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
