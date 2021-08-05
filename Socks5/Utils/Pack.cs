using Socks5.Enums;
using Socks5.Models;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Socks5.Utils
{
	public static class Pack
	{
		public static int DestinationAddressAndPort(string? hostName, IPAddress? ip, ushort port, Span<byte> destination)
		{
			// +------+----------+----------+
			// | ATYP | DST.ADDR | DST.PORT |
			// +------+----------+----------+
			// |  1   | Variable |    2     |
			// +------+----------+----------+

			int outLength;
			if (ip is null && !IPAddress.TryParse(hostName, out ip))
			{
				destination[0] = Convert.ToByte(AddressType.Domain);
				var length = Encoding.UTF8.GetBytes(hostName, destination[2..]);
				if (length > byte.MaxValue)
				{
					throw new ArgumentException($@"Domain Length > {byte.MaxValue}");
				}
				destination[1] = (byte)length;
				outLength = 1 + 1 + length;
			}
			else
			{
				if (!ip.TryWriteBytes(destination[1..], out var length))
				{
					throw new ArgumentOutOfRangeException(nameof(destination));
				}

				var type = length == 4 ? AddressType.IPv4 : AddressType.IPv6;
				destination[0] = Convert.ToByte(type);

				outLength = 1 + length;
			}

			BinaryPrimitives.WriteUInt16BigEndian(destination[outLength..], port);
			outLength += 2;

			return outLength;
		}

		public static int Handshake(Method serverMethod, Span<byte> buffer)
		{
			// +----+--------+
			// |VER | METHOD |
			// +----+--------+
			// | 1  |   1    |
			// +----+--------+

			buffer[0] = Constants.ProtocolVersion;
			buffer[1] = Convert.ToByte(serverMethod);
			return 2;
		}

		public static int Handshake(IReadOnlyList<Method> clientMethods, Span<byte> buffer)
		{
			// +----+----------+----------+
			// |VER | NMETHODS | METHODS  |
			// +----+----------+----------+
			// | 1  |    1     | 1 to 255 |
			// +----+----------+----------+

			if (clientMethods.Count > byte.MaxValue)
			{
				throw new ArgumentException($@"{nameof(clientMethods)}.Count > {byte.MaxValue}");
			}

			buffer[0] = Constants.ProtocolVersion;
			buffer[1] = (byte)clientMethods.Count;

			var outLength = 2;
			foreach (var method in clientMethods)
			{
				buffer[outLength++] = (byte)method;
			}

			return outLength;
		}

		public static int UsernamePasswordAuth(UsernamePassword credential, Span<byte> buffer)
		{
			// +----+------+----------+------+----------+
			// |VER | ULEN |  UNAME   | PLEN |  PASSWD  |
			// +----+------+----------+------+----------+
			// | 1  |  1   | 1 to 255 |  1   | 1 to 255 |
			// +----+------+----------+------+----------+

			buffer[0] = Constants.ProtocolVersion;
			var offset = 1;

			var usernameLength = Encoding.UTF8.GetBytes(credential.UserName, buffer[(offset + 1)..]);
			if (usernameLength > byte.MaxValue)
			{
				throw new ArgumentException($@"{nameof(credential.UserName)} too long.");
			}
			buffer[offset++] = (byte)usernameLength;
			offset += usernameLength;

			var passwordLength = Encoding.UTF8.GetBytes(credential.Password, buffer[(offset + 1)..]);
			if (passwordLength > byte.MaxValue)
			{
				throw new ArgumentException($@"{nameof(credential.Password)} too long.");
			}
			buffer[offset++] = (byte)passwordLength;
			offset += passwordLength;

			return offset;
		}

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

			return 3 + DestinationAddressAndPort(bound.Domain, bound.Address, bound.Port, buffer[3..]);
		}

		public static int ClientCommand(
			Command command,
			string? hostName, IPAddress? ip, ushort port,
			Span<byte> buffer)
		{
			// +----+-----+-------+------+----------+----------+
			// |VER | CMD |  RSV  | ATYP | DST.ADDR | DST.PORT |
			// +----+-----+-------+------+----------+----------+
			// | 1  |  1  | X'00' |  1   | Variable |    2     |
			// +----+-----+-------+------+----------+----------+

			buffer[0] = Constants.ProtocolVersion;
			buffer[1] = (byte)command;
			buffer[2] = Constants.Rsv;

			return 3 + DestinationAddressAndPort(hostName, ip, port, buffer[3..]);
		}

		public static int Udp(Span<byte> buffer, string? dst, IPAddress? dstAddress, ushort dstPort, ReadOnlySpan<byte> data, byte fragment = 0)
		{
			// +----+------+------+----------+----------+----------+
			// |RSV | FRAG | ATYP | DST.ADDR | DST.PORT |   DATA   |
			// +----+------+------+----------+----------+----------+
			// | 2  |  1   |  1   | Variable |    2     | Variable |
			// +----+------+------+----------+----------+----------+

			buffer[0] = Constants.Rsv;
			buffer[1] = Constants.Rsv;
			buffer[2] = fragment;
			var offset = 3;

			offset += DestinationAddressAndPort(dst, dstAddress, dstPort, buffer[3..]);

			data.CopyTo(buffer[offset..]);

			return offset + data.Length;
		}

		public static int DnsDomain(string domain, Span<byte> buffer)
		{
			var s = domain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

			var offset = 0;

			foreach (var sub in s)
			{
				buffer[offset++] = Convert.ToByte(sub.Length);
				offset += Encoding.UTF8.GetBytes(sub, buffer[offset..]);
			}

			buffer[offset++] = byte.MinValue;
			return offset;
		}
	}
}
