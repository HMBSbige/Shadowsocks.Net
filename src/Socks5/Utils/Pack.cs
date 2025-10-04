using Microsoft;
using Socks5.Enums;
using Socks5.Models;
using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Socks5.Utils;

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
			int length = Encoding.UTF8.GetBytes(hostName, destination[2..]);
			Requires.Argument(length <= byte.MaxValue, nameof(hostName), @"Domain Length > {0}", byte.MaxValue);
			destination[1] = (byte)length;
			outLength = 1 + 1 + length;
		}
		else
		{
			Requires.Argument(ip.TryWriteBytes(destination[1..], out int length), nameof(destination), @"buffer is too small");

			AddressType type = length == 4 ? AddressType.IPv4 : AddressType.IPv6;
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

		Requires.Argument(
			clientMethods.Count <= byte.MaxValue,
			nameof(clientMethods),
			@"{0}.Count > {1}",
			nameof(clientMethods),
			byte.MaxValue
		);

		buffer[0] = Constants.ProtocolVersion;
		buffer[1] = (byte)clientMethods.Count;

		int outLength = 2;
		foreach (Method method in clientMethods)
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

		buffer[0] = Constants.AuthVersion;
		int offset = 1;

		int usernameLength = Encoding.UTF8.GetBytes(credential.UserName, buffer[(offset + 1)..]);
		Requires.Argument(usernameLength <= byte.MaxValue, nameof(credential), @"{0} too long.", nameof(credential.UserName));
		buffer[offset++] = (byte)usernameLength;
		offset += usernameLength;

		int passwordLength = Encoding.UTF8.GetBytes(credential.Password, buffer[(offset + 1)..]);
		Requires.Argument(passwordLength <= byte.MaxValue, nameof(credential), @"{0} too long.", nameof(credential.Password));
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
		int offset = 3;

		offset += DestinationAddressAndPort(dst, dstAddress, dstPort, buffer[3..]);

		data.CopyTo(buffer[offset..]);

		return offset + data.Length;
	}

	public static int DnsDomain(string domain, Span<byte> buffer)
	{
		string[] s = domain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		int offset = 0;

		foreach (string sub in s)
		{
			buffer[offset++] = Convert.ToByte(sub.Length);
			offset += Encoding.UTF8.GetBytes(sub, buffer[offset..]);
		}

		buffer[offset++] = byte.MinValue;
		return offset;
	}
}
