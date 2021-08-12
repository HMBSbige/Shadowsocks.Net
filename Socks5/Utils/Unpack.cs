using Microsoft;
using Socks5.Enums;
using Socks5.Exceptions;
using Socks5.Models;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Socks5.Utils
{
	public static class Unpack
	{
		public static bool ReadResponseMethod(
			ref ReadOnlySequence<byte> buffer,
			out Method method)
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

			var reader = new SequenceReader<byte>(buffer);

			reader.TryRead(out var b0);
			if (b0 is not Constants.ProtocolVersion)
			{
				throw new ProtocolErrorException($@"Server version is not 0x05: 0x{b0:X2}.");
			}

			reader.TryRead(out var b1);
			method = (Method)b1;
			if (!Enum.IsDefined(typeof(Method), method))
			{
				throw new ProtocolErrorException($@"Server sent an unknown method: 0x{b1:X2}.");
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

			var reader = new SequenceReader<byte>(buffer);

			reader.TryRead(out var b0);
			if (b0 is not Constants.AuthVersion)
			{
				throw new ProtocolErrorException($@"Authentication version is not 0x01: 0x{b0:X2}.");
			}

			reader.TryRead(out var status);
			if (status is not 0x00)
			{
				throw new AuthenticationFailureException($@"Authentication failed: {status}.", status);
			}

			buffer = buffer.Slice(2);
			return true;
		}

		public static int DestinationAddress(AddressType type, ReadOnlySpan<byte> bytes,
			out IPAddress? address, out string? domain)
		{
			address = null;
			domain = null;
			int offset;

			switch (type)
			{
				case AddressType.IPv4:
				{
					offset = Constants.IPv4AddressBytesLength;
					address = new IPAddress(bytes[..offset]);
					break;
				}
				case AddressType.IPv6:
				{
					offset = Constants.IPv6AddressBytesLength;
					address = new IPAddress(bytes[..offset]);
					break;
				}
				case AddressType.Domain:
				{
					offset = bytes[0];
					domain = Encoding.UTF8.GetString(bytes.Slice(1, offset));
					++offset;
					break;
				}
				default:
				{
					throw new ProtocolErrorException($@"Server reply an unknown address type: {type}.");
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

			var span = buffer.Span;
			Requires.Range(buffer.Length >= 7, nameof(buffer));

			var res = new Socks5UdpReceivePacket();

			if (span[0] is not Constants.Rsv || span[1] is not Constants.Rsv)
			{
				throw new ProtocolErrorException($@"Protocol failed, RESERVED is not 0x0000: 0x{span[0]:X2}{span[1]:X2}.");
			}

			res.Fragment = span[2];

			res.Type = (AddressType)span[3];
			Requires.Defined(res.Type, nameof(res.Type));

			var offset = 4;
			offset += DestinationAddress(res.Type, span[offset..], out res.Address, out res.Domain);

			res.Port = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
			res.Data = MemoryMarshal.AsMemory(buffer[(offset + 2)..]);

			return res;
		}

		public static bool ReadDestinationAddress(
			ref this SequenceReader<byte> reader,
			AddressType type,
			out IPAddress? address, out string? domain)
		{
			address = null;
			domain = null;

			var length = Constants.IPv6AddressBytesLength;
			switch (type)
			{
				case AddressType.IPv4:
				{
					length = Constants.IPv4AddressBytesLength;
					goto case AddressType.IPv6;
				}
				case AddressType.IPv6:
				{
					if (reader.Remaining < length)
					{
						return false;
					}

					Span<byte> temp = stackalloc byte[length];
					reader.UnreadSequence.Slice(0, length).CopyTo(temp);
					address = new IPAddress(temp);

					reader.Advance(length);
					return true;
				}
				case AddressType.Domain:
				{
					if (!reader.TryRead(out var domainLength))
					{
						return false;
					}

					if (reader.Remaining < domainLength)
					{
						return false;
					}

					domain = Encoding.UTF8.GetString(reader.UnreadSequence.Slice(0, domainLength));

					reader.Advance(domainLength);
					return true;
				}
				default:
				{
					throw new ProtocolErrorException($@"Server reply an unknown address type: {type}.");
				}
			}
		}

		public static bool ReadServerReplyCommand(
			ref ReadOnlySequence<byte> buffer, out ServerBound bound)
		{
			// +----+-----+-------+------+----------+----------+
			// |VER | REP |  RSV  | ATYP | BND.ADDR | BND.PORT |
			// +----+-----+-------+------+----------+----------+
			// | 1  |  1  | X'00' |  1   | Variable |    2     |
			// +----+-----+-------+------+----------+----------+

			bound = default;
			var reader = new SequenceReader<byte>(buffer);

			if (buffer.Length < 1 + 1 + 1 + 1 + 1 + 2)
			{
				return false;
			}

			reader.TryRead(out var b0);
			if (b0 is not Constants.ProtocolVersion)
			{
				throw new ProtocolErrorException($@"Server version is not 0x05: 0x{b0:X2}.");
			}

			reader.TryRead(out var b1);
			var reply = (Socks5Reply)b1;
			if (reply is not Socks5Reply.Succeeded)
			{
				throw new ProtocolErrorException($@"Protocol failed, server reply: {reply}.", reply);
			}

			reader.TryRead(out var b2);
			if (b2 is not Constants.Rsv)
			{
				throw new ProtocolErrorException($@"Protocol failed, RESERVED is not 0x00: 0x{b2:X2}.");
			}

			reader.TryRead(out var b3);
			bound.Type = (AddressType)b3;

			if (!reader.ReadDestinationAddress(bound.Type, out bound.Address, out bound.Domain))
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

		public static bool ReadClientHandshake(ref ReadOnlySequence<byte> buffer, ref HashSet<Method> methods)
		{
			// +----+----------+----------+
			// |VER | NMETHODS | METHODS  |
			// +----+----------+----------+
			// | 1  |    1     | 1 to 255 |
			// +----+----------+----------+

			var reader = new SequenceReader<byte>(buffer);
			if (!reader.TryRead(out var ver))
			{
				return false;
			}

			if (ver is not Constants.ProtocolVersion)
			{
				throw new ProtocolErrorException($@"Server version is not 0x05: 0x{ver:X2}.");
			}

			if (!reader.TryRead(out var num))
			{
				return false;
			}

			if (reader.Remaining < num)
			{
				return false;
			}

			for (var i = 0; i < num; ++i)
			{
				reader.TryRead(out var b);
				methods.Add((Method)b);
			}

			buffer = buffer.Slice(reader.Consumed);
			return true;
		}

		public static bool ReadClientAuth(ref ReadOnlySequence<byte> buffer, ref UsernamePassword? clientCredential)
		{
			// +----+------+----------+------+----------+
			// |VER | ULEN |  UNAME   | PLEN |  PASSWD  |
			// +----+------+----------+------+----------+
			// | 1  |  1   | 1 to 255 |  1   | 1 to 255 |
			// +----+------+----------+------+----------+

			var reader = new SequenceReader<byte>(buffer);
			if (!reader.TryRead(out var ver))
			{
				return false;
			}

			if (ver is not Constants.AuthVersion)
			{
				throw new ProtocolErrorException($@"Client auth version is not 0x01: 0x{ver:X2}.");
			}

			if (!reader.TryRead(out var uLen))
			{
				return false;
			}

			if (reader.Remaining < uLen)
			{
				return false;
			}

			var username = Encoding.UTF8.GetString(reader.UnreadSequence.Slice(0, uLen));
			reader.Advance(uLen);

			if (!reader.TryRead(out var pLen))
			{
				return false;
			}

			if (reader.Remaining < pLen)
			{
				return false;
			}

			var password = Encoding.UTF8.GetString(reader.UnreadSequence.Slice(0, pLen));
			reader.Advance(pLen);

			clientCredential = new UsernamePassword
			{
				UserName = username,
				Password = password
			};
			buffer = buffer.Slice(reader.Consumed);
			return true;
		}
	}
}
