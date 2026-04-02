using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Socks5.Protocol;

internal static class Unpack
{
	public static bool ReadResponseMethod(ref ReadOnlySequence<byte> buffer, out Method method)
	{
		// +----+--------+
		// |VER | METHOD |
		// +----+--------+
		// | 1  |   1    |
		// +----+--------+

		method = Method.NoAcceptable;

		if (buffer.Length < 2)
		{
			return false;
		}

		SequenceReader<byte> reader = new(buffer);

		reader.TryRead(out byte b0);

		if (b0 is not Constants.ProtocolVersion)
		{
			throw new Socks5ProtocolErrorException($@"Server version is not 0x05: 0x{b0:X2}.", Socks5Reply.GeneralFailure);
		}

		reader.TryRead(out byte b1);
		method = (Method)b1;

		if (!Enum.IsDefined(method))
		{
			throw new Socks5ProtocolErrorException($@"Server sent an unknown method: 0x{b1:X2}.", Socks5Reply.ConnectionNotAllowed);
		}

		buffer = buffer.Slice(2);
		return true;
	}

	public static bool ReadResponseAuthReply(ref ReadOnlySequence<byte> buffer)
	{
		// +----+--------+
		// |VER | STATUS |
		// +----+--------+
		// | 1  |   1    |
		// +----+--------+

		if (buffer.Length < 2)
		{
			return false;
		}

		SequenceReader<byte> reader = new(buffer);

		reader.TryRead(out byte b0);

		if (b0 is not Constants.AuthVersion)
		{
			throw new Socks5ProtocolErrorException($@"Authentication version is not 0x01: 0x{b0:X2}.", Socks5Reply.ConnectionNotAllowed);
		}

		reader.TryRead(out byte status);

		if (status is not 0x00)
		{
			throw new AuthenticationFailureException($@"Authentication failed: {status}.", status);
		}

		buffer = buffer.Slice(2);
		return true;
	}

	public static int DestinationAddress(AddressType type, ReadOnlySpan<byte> bytes, Span<byte> hostBuffer, out int hostBytesWritten)
	{
		int offset;

		switch (type)
		{
			case AddressType.IPv4:
			case AddressType.IPv6:
			{
				offset = type is AddressType.IPv4 ? Constants.IPv4AddressBytesLength : Constants.IPv6AddressBytesLength;
				if (bytes.Length < offset)
				{
					throw new Socks5ProtocolErrorException($@"Truncated {type} address: expected {offset} bytes, got {bytes.Length}.", Socks5Reply.GeneralFailure);
				}
				FormatIPAddress(bytes.Slice(0, offset), hostBuffer, out hostBytesWritten);
				break;
			}
			case AddressType.Domain:
			{
				if (bytes.Length < 1)
				{
					throw new Socks5ProtocolErrorException("Truncated domain address: missing length byte.", Socks5Reply.GeneralFailure);
				}
				// Length 0 is intentionally accepted: RFC 1928 §4 does not
				// explicitly require a minimum length, and downstream resolution
				// will fail naturally for empty names.
				offset = bytes[0];
				if (bytes.Length < 1 + offset)
				{
					throw new Socks5ProtocolErrorException($@"Truncated domain address: expected {offset} bytes, got {bytes.Length - 1}.", Socks5Reply.GeneralFailure);
				}
				bytes.Slice(1, offset).CopyTo(hostBuffer);
				hostBytesWritten = offset;
				++offset; // skip length byte
				break;
			}
			default:
			{
				throw new Socks5ProtocolErrorException($@"Server reply an unknown address type: {type}.", Socks5Reply.AddressTypeNotSupported);
			}
		}

		return offset;
	}

	public static Socks5UdpReceivePacket Udp(ReadOnlyMemory<byte> buffer)
	{
		// +----+------+------+----------+----------+----------+
		// |RSV | FRAG | ATYP | DST.ADDR | DST.PORT |   DATA   |
		// +----+------+------+----------+----------+----------+
		// | 2  |  1   |  1   | Variable |    2     | Variable |
		// +----+------+------+----------+----------+----------+

		ReadOnlySpan<byte> span = buffer.Span;
		if (buffer.Length < 4)
		{
			throw new Socks5ProtocolErrorException($@"UDP packet too short: {buffer.Length} bytes, minimum 4.", Socks5Reply.GeneralFailure);
		}

		Socks5UdpReceivePacket res = new();

		if (span[0] is not Constants.Rsv || span[1] is not Constants.Rsv)
		{
			throw new Socks5ProtocolErrorException($@"Protocol failed, RESERVED is not 0x0000: 0x{span[0]:X2}{span[1]:X2}.", Socks5Reply.GeneralFailure);
		}

		res.Fragment = span[2];

		res.Type = (AddressType)span[3];

		if (!Enum.IsDefined(res.Type))
		{
			throw new Socks5ProtocolErrorException($@"Unknown address type: 0x{span[3]:X2}.", Socks5Reply.AddressTypeNotSupported);
		}

		int offset = 4;
		offset += DestinationAddress(res.Type, span.Slice(offset), res.Host.WriteBuffer, out res.Host.Length);

		if (buffer.Length < offset + 2)
		{
			throw new Socks5ProtocolErrorException($@"UDP packet truncated before port: need {offset + 2} bytes, got {buffer.Length}.", Socks5Reply.GeneralFailure);
		}
		res.Port = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset));
		res.Data = buffer.Slice(offset + 2);

		return res;
	}

	public static bool ReadDestinationAddress(ref SequenceReader<byte> reader, AddressType type, scoped Span<byte> hostBuffer, out int hostBytesWritten)
	{
		hostBytesWritten = 0;

		switch (type)
		{
			case AddressType.IPv4:
			case AddressType.IPv6:
			{
				int length = type is AddressType.IPv4 ? Constants.IPv4AddressBytesLength : Constants.IPv6AddressBytesLength;

				if (reader.Remaining < length)
				{
					return false;
				}

				Span<byte> temp = stackalloc byte[length];
				reader.UnreadSequence.Slice(0, length).CopyTo(temp);
				FormatIPAddress(temp, hostBuffer, out hostBytesWritten);

				reader.Advance(length);
				return true;
			}
			case AddressType.Domain:
			{
				// See DestinationAddress for rationale on accepting length 0.
				if (!reader.TryRead(out byte domainLength))
				{
					return false;
				}

				if (reader.Remaining < domainLength)
				{
					reader.Rewind(1);
					return false;
				}

				reader.UnreadSequence.Slice(0, domainLength).CopyTo(hostBuffer);
				hostBytesWritten = domainLength;

				reader.Advance(domainLength);
				return true;
			}
			default:
			{
				throw new Socks5ProtocolErrorException($@"Server reply an unknown address type: {type}.", Socks5Reply.AddressTypeNotSupported);
			}
		}
	}

	public static bool ReadServerReplyCommand(ref ReadOnlySequence<byte> buffer, out ServerBound bound)
	{
		// +----+-----+-------+------+----------+----------+
		// |VER | REP |  RSV  | ATYP | BND.ADDR | BND.PORT |
		// +----+-----+-------+------+----------+----------+
		// | 1  |  1  | X'00' |  1   | Variable |    2     |
		// +----+-----+-------+------+----------+----------+

		bound = default;
		SequenceReader<byte> reader = new(buffer);

		if (buffer.Length < 1 + 1 + 1 + 1 + 1 + 2)
		{
			return false;
		}

		reader.TryRead(out byte b0);

		if (b0 is not Constants.ProtocolVersion)
		{
			throw new Socks5ProtocolErrorException($@"Server version is not 0x05: 0x{b0:X2}.", Socks5Reply.GeneralFailure);
		}

		reader.TryRead(out byte b1);
		Socks5Reply reply = (Socks5Reply)b1;

		if (reply is not Socks5Reply.Succeeded)
		{
			throw new Socks5ProtocolErrorException($@"Protocol failed, server reply: {reply}.", reply);
		}

		reader.TryRead(out byte b2);

		if (b2 is not Constants.Rsv)
		{
			throw new Socks5ProtocolErrorException($@"Protocol failed, RESERVED is not 0x00: 0x{b2:X2}.", Socks5Reply.GeneralFailure);
		}

		reader.TryRead(out byte b3);
		bound.Type = (AddressType)b3;

		if (!ReadDestinationAddress(ref reader, bound.Type, bound.Host.WriteBuffer, out bound.Host.Length))
		{
			return false;
		}

		if (!reader.TryReadBigEndian(out short port))
		{
			return false;
		}

		bound.Port = (ushort)port;

		buffer = buffer.Slice(reader.Consumed);
		return true;
	}

	public static bool ReadClientHandshake(ref ReadOnlySequence<byte> buffer, Method target, out Method selected)
	{
		// +----+----------+----------+
		// |VER | NMETHODS | METHODS  |
		// +----+----------+----------+
		// | 1  |    1     | 1 to 255 |
		// +----+----------+----------+

		selected = Method.NoAcceptable;

		SequenceReader<byte> reader = new(buffer);

		if (!reader.TryRead(out byte ver))
		{
			return false;
		}

		if (ver is not Constants.ProtocolVersion)
		{
			throw new Socks5ProtocolErrorException($@"Client version is not 0x05: 0x{ver:X2}.", Socks5Reply.GeneralFailure);
		}

		if (!reader.TryRead(out byte num))
		{
			return false;
		}

		if (num is 0)
		{
			throw new Socks5ProtocolErrorException("NMETHODS is 0 (RFC 1928 §3 requires 1–255 methods).", Socks5Reply.GeneralFailure);
		}

		if (!reader.TryReadExact(num, out ReadOnlySequence<byte> methods))
		{
			return false;
		}

		if (methods.PositionOf((byte)target) is not null)
		{
			selected = target;
		}

		buffer = buffer.Slice(reader.Consumed);
		return true;
	}

	public static bool ReadClientAuth(ref ReadOnlySequence<byte> buffer, [NotNullWhen(true)] ref UserPassAuth? clientCredential)
	{
		// +----+------+----------+------+----------+
		// |VER | ULEN |  UNAME   | PLEN |  PASSWD  |
		// +----+------+----------+------+----------+
		// | 1  |  1   | 1 to 255 |  1   | 1 to 255 |
		// +----+------+----------+------+----------+

		SequenceReader<byte> reader = new(buffer);

		if (!reader.TryRead(out byte ver))
		{
			return false;
		}

		if (ver is not Constants.AuthVersion)
		{
			throw new Socks5ProtocolErrorException($@"Client auth version is not 0x01: 0x{ver:X2}.", Socks5Reply.ConnectionNotAllowed);
		}

		if (!reader.TryRead(out byte uLen))
		{
			return false;
		}

		if (uLen is 0)
		{
			throw new Socks5ProtocolErrorException("ULEN must be 1–255 (RFC 1929 §2).", Socks5Reply.ConnectionNotAllowed);
		}

		if (reader.Remaining < uLen)
		{
			return false;
		}

		byte[] username = reader.UnreadSequence.Slice(0, uLen).ToArray();
		reader.Advance(uLen);

		if (!reader.TryRead(out byte pLen))
		{
			return false;
		}

		if (pLen is 0)
		{
			throw new Socks5ProtocolErrorException("PLEN must be 1–255 (RFC 1929 §2).", Socks5Reply.ConnectionNotAllowed);
		}

		if (reader.Remaining < pLen)
		{
			return false;
		}

		byte[] password = reader.UnreadSequence.Slice(0, pLen).ToArray();
		reader.Advance(pLen);

		clientCredential = new UserPassAuth
		{
			UserName = username,
			Password = password
		};
		buffer = buffer.Slice(reader.Consumed);
		return true;
	}

	private static void FormatIPAddress(ReadOnlySpan<byte> address, Span<byte> destination, out int bytesWritten)
	{
		if (!new IPAddress(address).TryFormat(destination, out bytesWritten))
		{
			throw new Socks5ProtocolErrorException(
				"Failed to format IP address into host buffer.",
				Socks5Reply.GeneralFailure);
		}
	}
}
