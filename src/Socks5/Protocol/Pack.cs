using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Socks5.Protocol;

/// <summary>
/// Packs SOCKS5 protocol messages into byte buffers.
/// </summary>
internal static class Pack
{
	/// <summary>
	/// Packs ATYP + DST.ADDR + DST.PORT into <paramref name="destination"/>.
	/// <paramref name="hostText"/> is either a formatted IP address or a domain name.
	/// </summary>
	public static int DestinationAddressAndPort(ReadOnlySpan<byte> hostText, ushort port, Span<byte> destination)
	{
		// +------+----------+----------+
		// | ATYP | DST.ADDR | DST.PORT |
		// +------+----------+----------+
		// |  1   | Variable |    2     |
		// +------+----------+----------+

		int outLength;

		if (IPAddress.TryParse(hostText, out IPAddress? addr))
		{
			AddressType type = addr.AddressFamily is AddressFamily.InterNetworkV6 ? AddressType.IPv6 : AddressType.IPv4;
			destination[0] = (byte)type;

			if (!addr.TryWriteBytes(destination.Slice(1), out int addrLen))
			{
				throw new ArgumentException("Destination buffer is too small.", nameof(destination));
			}

			outLength = 1 + addrLen;
		}
		else
		{
			if (hostText.Length > byte.MaxValue)
			{
				throw new ArgumentException($"Domain length > {byte.MaxValue}", nameof(hostText));
			}

			destination[0] = (byte)AddressType.Domain;
			destination[1] = (byte)hostText.Length;
			hostText.CopyTo(destination.Slice(2));
			outLength = 1 + 1 + hostText.Length;
		}

		BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(outLength), port);
		outLength += 2;

		return outLength;
	}

	/// <summary>
	/// Packs the server's chosen method response (VER + METHOD).
	/// </summary>
	public static int Handshake(Method serverMethod, Span<byte> buffer)
	{
		// +----+--------+
		// |VER | METHOD |
		// +----+--------+
		// | 1  |   1    |
		// +----+--------+

		buffer[0] = Constants.ProtocolVersion;
		buffer[1] = (byte)serverMethod;
		return 2;
	}

	/// <summary>
	/// Packs the client handshake (VER + NMETHODS + METHODS).
	/// </summary>
	public static int Handshake(ReadOnlySpan<Method> clientMethods, Span<byte> buffer)
	{
		// +----+----------+----------+
		// |VER | NMETHODS | METHODS  |
		// +----+----------+----------+
		// | 1  |    1     | 1 to 255 |
		// +----+----------+----------+

		if (clientMethods.Length > byte.MaxValue)
		{
			throw new ArgumentException($"{nameof(clientMethods)}.Length > {byte.MaxValue}", nameof(clientMethods));
		}

		buffer[0] = Constants.ProtocolVersion;
		buffer[1] = (byte)clientMethods.Length;

		int outLength = 2;

		foreach (Method method in clientMethods)
		{
			buffer[outLength++] = (byte)method;
		}

		return outLength;
	}

	/// <summary>
	/// Packs a username/password authentication sub-negotiation request (RFC 1929).
	/// </summary>
	public static int UsernamePasswordAuth(UserPassAuth credential, Span<byte> buffer)
	{
		// +----+------+----------+------+----------+
		// |VER | ULEN |  UNAME   | PLEN |  PASSWD  |
		// +----+------+----------+------+----------+
		// | 1  |  1   | 1 to 255 |  1   | 1 to 255 |
		// +----+------+----------+------+----------+

		buffer[0] = Constants.AuthVersion;
		int offset = 1;

		ReadOnlySpan<byte> username = credential.UserName.Span;

		if (username.Length is 0 or > byte.MaxValue)
		{
			throw new ArgumentException($"{nameof(credential.UserName)} must be 1–255 bytes.", nameof(credential));
		}

		buffer[offset++] = (byte)username.Length;
		username.CopyTo(buffer.Slice(offset));
		offset += username.Length;

		ReadOnlySpan<byte> password = credential.Password.Span;

		if (password.Length is 0 or > byte.MaxValue)
		{
			throw new ArgumentException($"{nameof(credential.Password)} must be 1–255 bytes.", nameof(credential));
		}

		buffer[offset++] = (byte)password.Length;
		password.CopyTo(buffer.Slice(offset));
		offset += password.Length;

		return offset;
	}

	/// <summary>
	/// Packs a username/password authentication reply (VER + STATUS).
	/// </summary>
	public static int AuthReply(bool isSuccess, Span<byte> buffer)
	{
		// +----+--------+
		// |VER | STATUS |
		// +----+--------+
		// | 1  |   1    |
		// +----+--------+

		buffer[0] = Constants.AuthVersion;
		buffer[1] = Convert.ToByte(!isSuccess);
		return 2;
	}

	/// <summary>
	/// Packs a server reply (VER + REP + RSV + ATYP + BND.ADDR + BND.PORT).
	/// </summary>
	public static int ServerReply(Socks5Reply reply, ServerBound bound, Span<byte> buffer)
	{
		// +----+-----+-------+------+----------+----------+
		// |VER | REP |  RSV  | ATYP | BND.ADDR | BND.PORT |
		// +----+-----+-------+------+----------+----------+
		// | 1  |  1  | X'00' |  1   | Variable |    2     |
		// +----+-----+-------+------+----------+----------+

		buffer[0] = Constants.ProtocolVersion;
		buffer[1] = (byte)reply;
		buffer[2] = Constants.Rsv;

		return 3 + DestinationAddressAndPort(bound.Host.Span, bound.Port, buffer.Slice(3));
	}

	/// <summary>
	/// Packs a client command request (VER + CMD + RSV + ATYP + DST.ADDR + DST.PORT).
	/// </summary>
	public static int ClientCommand(Command command, ReadOnlySpan<byte> hostText, ushort port, Span<byte> buffer)
	{
		// +----+-----+-------+------+----------+----------+
		// |VER | CMD |  RSV  | ATYP | DST.ADDR | DST.PORT |
		// +----+-----+-------+------+----------+----------+
		// | 1  |  1  | X'00' |  1   | Variable |    2     |
		// +----+-----+-------+------+----------+----------+

		buffer[0] = Constants.ProtocolVersion;
		buffer[1] = (byte)command;
		buffer[2] = Constants.Rsv;

		return 3 + DestinationAddressAndPort(hostText, port, buffer.Slice(3));
	}

	/// <summary>
	/// Packs a UDP relay header (RSV + FRAG + ATYP + DST.ADDR + DST.PORT) followed by <paramref name="data"/>.
	/// </summary>
	public static int Udp(Span<byte> buffer, ReadOnlySpan<byte> hostText, ushort dstPort, ReadOnlySpan<byte> data, byte fragment = 0)
	{
		// +----+------+------+----------+----------+----------+
		// |RSV | FRAG | ATYP | DST.ADDR | DST.PORT |   DATA   |
		// +----+------+------+----------+----------+----------+
		// | 2  |  1   |  1   | Variable |    2     | Variable |
		// +----+------+------+----------+----------+----------+

		buffer[0] = Constants.Rsv;
		buffer[1] = Constants.Rsv;
		buffer[2] = fragment;
		int offset = 3;

		offset += DestinationAddressAndPort(hostText, dstPort, buffer.Slice(3));

		data.CopyTo(buffer.Slice(offset));

		return offset + data.Length;
	}

	/// <summary>
	/// Packs a domain name in DNS label format (length-prefixed labels terminated by a zero byte).
	/// </summary>
	public static int DnsDomain(ReadOnlySpan<char> domain, Span<byte> buffer)
	{
		if (!Ascii.IsValid(domain))
		{
			throw new ArgumentException("DNS labels must be ASCII.", nameof(domain));
		}

		if (domain.EndsWith("."))
		{
			domain = domain.Slice(0, domain.Length - 1);
		}

		if (domain.IsEmpty)
		{
			throw new ArgumentException("Domain must not be empty.", nameof(domain));
		}

		int wireLength = domain.Length + 2; // first length byte + null terminator; dots become length bytes in-place

		if (wireLength > 255)
		{
			throw new ArgumentException($"DNS name wire length must be <= 255, got {wireLength}.", nameof(domain));
		}

		if (buffer.Length < wireLength)
		{
			throw new ArgumentException("Destination buffer is too small.", nameof(buffer));
		}

		// Bulk copy domain into buffer[1..], leaving buffer[0] for first label length.
		Ascii.FromUtf16(domain, buffer.Slice(1), out _);

		// Patch: replace each '.' (0x2E) with the preceding label's length.
		int labelStart = 0;
		int end = domain.Length + 1;

		for (int i = 1; i < end; ++i)
		{
			if (buffer[i] is not (byte)'.')
			{
				continue;
			}

			int labelLen = i - labelStart - 1;

			if (labelLen is 0 or > 63)
			{
				throw new ArgumentException($"DNS label length must be 1-63, got {labelLen}.", nameof(domain));
			}

			buffer[labelStart] = (byte)labelLen;
			labelStart = i;
		}

		// Last label.
		int lastLabelLen = end - labelStart - 1;

		if (lastLabelLen is 0 or > 63)
		{
			throw new ArgumentException($"DNS label length must be 1-63, got {lastLabelLen}.", nameof(domain));
		}

		buffer[labelStart] = (byte)lastLabelLen;
		buffer[end] = byte.MinValue;

		return wireLength;
	}
}
