using Socks5;
using Socks5.Protocol;
using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace UnitTest;

public class Socks5PackTest
{
	[Test]
	public async Task DestinationAddressAndPort_IPv4(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[64];
		int len = Pack.DestinationAddressAndPort("127.0.0.1"u8, 80, buffer);

		await Assert.That(len).IsEqualTo(7);
		await Assert.That(buffer[0]).IsEqualTo((byte)AddressType.IPv4);
		await Assert.That(buffer[1]).IsEqualTo((byte)0x7F);
		await Assert.That(buffer[2]).IsEqualTo((byte)0x00);
		await Assert.That(buffer[3]).IsEqualTo((byte)0x00);
		await Assert.That(buffer[4]).IsEqualTo((byte)0x01);
		await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan().Slice(5))).IsEqualTo((ushort)80);
	}

	[Test]
	public async Task DestinationAddressAndPort_IPv6(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[64];
		int len = Pack.DestinationAddressAndPort("::1"u8, 443, buffer);

		await Assert.That(len).IsEqualTo(19); // 1 ATYP + 16 addr + 2 port
		await Assert.That(buffer[0]).IsEqualTo((byte)AddressType.IPv6);
		await Assert.That(buffer[16]).IsEqualTo((byte)0x01); // last byte of ::1
		await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan().Slice(17))).IsEqualTo((ushort)443);
	}

	[Test]
	public async Task DestinationAddressAndPort_Domain(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[64];
		int len = Pack.DestinationAddressAndPort("example.com"u8, 80, buffer);

		await Assert.That(len).IsEqualTo(15); // 1 ATYP + 1 len + 11 domain + 2 port
		await Assert.That(buffer[0]).IsEqualTo((byte)AddressType.Domain);
		await Assert.That(buffer[1]).IsEqualTo((byte)11);
		await Assert.That(Encoding.ASCII.GetString(buffer.AsSpan().Slice(2, 11))).IsEqualTo("example.com");
		await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan().Slice(13))).IsEqualTo((ushort)80);
	}

	[Test]
	[DisplayName("DestinationAddressAndPort: IPv4-mapped IPv6 encodes as ATYP=IPv4")]
	public async Task DestinationAddressAndPort_IPv4MappedIPv6_EncodesAsIPv4(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[64];
		int len = Pack.DestinationAddressAndPort("::ffff:1.2.3.4"u8, 80, buffer);

		await Assert.That(len).IsEqualTo(7); // 1 ATYP + 4 addr + 2 port
		await Assert.That(buffer[0]).IsEqualTo((byte)AddressType.IPv4);
		await Assert.That(buffer[1]).IsEqualTo((byte)1);
		await Assert.That(buffer[2]).IsEqualTo((byte)2);
		await Assert.That(buffer[3]).IsEqualTo((byte)3);
		await Assert.That(buffer[4]).IsEqualTo((byte)4);
		await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan().Slice(5))).IsEqualTo((ushort)80);
	}

	[Test]
	[DisplayName("DestinationAddressAndPort: empty non-IP host rejects empty domain name")]
	public async Task DestinationAddressAndPort_EmptyDomain_Throws(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[64];

		await Assert.That(() => Pack.DestinationAddressAndPort(ReadOnlySpan<byte>.Empty, 80, buffer)).Throws<ArgumentException>();
	}

	[Test]
	public async Task DestinationAddressAndPort_DomainTooLong(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[512];
		byte[] longDomain = new byte[256];
		longDomain.AsSpan().Fill((byte)'a');

		await Assert.That(() => Pack.DestinationAddressAndPort(longDomain, 80, buffer)).Throws<ArgumentException>();
	}

	[Test]
	public async Task Handshake_ServerMethod(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[8];
		int len = Pack.Handshake(Method.UsernamePassword, buffer);

		await Assert.That(len).IsEqualTo(2);
		await Assert.That(buffer[0]).IsEqualTo(Constants.ProtocolVersion);
		await Assert.That(buffer[1]).IsEqualTo((byte)Method.UsernamePassword);
	}

	[Test]
	public async Task Handshake_ClientMethods(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[16];
		Method[] methods = [Method.NoAuthentication, Method.UsernamePassword];
		int len = Pack.Handshake(methods, buffer);

		await Assert.That(len).IsEqualTo(4);
		await Assert.That(buffer[0]).IsEqualTo(Constants.ProtocolVersion);
		await Assert.That(buffer[1]).IsEqualTo((byte)2);
		await Assert.That(buffer[2]).IsEqualTo((byte)Method.NoAuthentication);
		await Assert.That(buffer[3]).IsEqualTo((byte)Method.UsernamePassword);
	}

	[Test]
	public async Task Handshake_ClientTooManyMethods(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[512];
		Method[] methods = new Method[256];

		await Assert.That(() => Pack.Handshake(methods, buffer)).Throws<ArgumentException>();
	}

	[Test]
	public async Task UsernamePasswordAuth_Normal(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[Constants.MaxUsernamePasswordAuthLength];
		UserPassAuth cred = new() { UserName = "user"u8.ToArray(), Password = "pass"u8.ToArray() };
		int len = Pack.UsernamePasswordAuth(cred, buffer);

		await Assert.That(len).IsEqualTo(11); // 1 + 1 + 4 + 1 + 4
		await Assert.That(buffer[0]).IsEqualTo(Constants.AuthVersion);
		await Assert.That(buffer[1]).IsEqualTo((byte)4);
		await Assert.That(buffer.AsSpan().Slice(2, 4).SequenceEqual("user"u8)).IsTrue();
		await Assert.That(buffer[6]).IsEqualTo((byte)4);
		await Assert.That(buffer.AsSpan().Slice(7, 4).SequenceEqual("pass"u8)).IsTrue();
	}

	[Test]
	public async Task UsernamePasswordAuth_Unicode(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[Constants.MaxUsernamePasswordAuthLength];
		byte[] userBytes = "用户"u8.ToArray();
		byte[] passBytes = "密码"u8.ToArray();
		UserPassAuth cred = new() { UserName = userBytes, Password = passBytes };
		int len = Pack.UsernamePasswordAuth(cred, buffer);

		int uLen = userBytes.Length;
		int pLen = passBytes.Length;
		await Assert.That(len).IsEqualTo(1 + 1 + uLen + 1 + pLen);
		await Assert.That(buffer[0]).IsEqualTo(Constants.AuthVersion);
		await Assert.That(buffer[1]).IsEqualTo((byte)uLen);
		await Assert.That(buffer.AsSpan().Slice(2, uLen).SequenceEqual(userBytes)).IsTrue();
		await Assert.That(buffer[2 + uLen]).IsEqualTo((byte)pLen);
		await Assert.That(buffer.AsSpan().Slice(3 + uLen, pLen).SequenceEqual(passBytes)).IsTrue();
	}

	[Test]
	public async Task UsernamePasswordAuth_UsernameTooLong(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[1024];
		UserPassAuth cred = new() { UserName = new byte[256], Password = "p"u8.ToArray() };

		await Assert.That(() => Pack.UsernamePasswordAuth(cred, buffer)).Throws<ArgumentException>();
	}

	[Test]
	public async Task UsernamePasswordAuth_PasswordTooLong(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[1024];
		UserPassAuth cred = new() { UserName = "u"u8.ToArray(), Password = new byte[256] };

		await Assert.That(() => Pack.UsernamePasswordAuth(cred, buffer)).Throws<ArgumentException>();
	}

	[Test]
	public async Task UsernamePasswordAuth_EmptyUsername(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[1024];
		UserPassAuth cred = new() { UserName = Array.Empty<byte>(), Password = "p"u8.ToArray() };

		await Assert.That(() => Pack.UsernamePasswordAuth(cred, buffer)).Throws<ArgumentException>();
	}

	[Test]
	public async Task UsernamePasswordAuth_EmptyPassword(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[1024];
		UserPassAuth cred = new() { UserName = "u"u8.ToArray(), Password = Array.Empty<byte>() };

		await Assert.That(() => Pack.UsernamePasswordAuth(cred, buffer)).Throws<ArgumentException>();
	}

	[Test]
	public async Task AuthReply_Success(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[4];
		int len = Pack.AuthReply(true, buffer);

		await Assert.That(len).IsEqualTo(2);
		await Assert.That(buffer[0]).IsEqualTo(Constants.AuthVersion);
		await Assert.That(buffer[1]).IsEqualTo(Constants.AuthStatusSuccess);
	}

	[Test]
	public async Task AuthReply_Failure(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[4];
		int len = Pack.AuthReply(false, buffer);

		await Assert.That(len).IsEqualTo(2);
		await Assert.That(buffer[0]).IsEqualTo(Constants.AuthVersion);
		await Assert.That(buffer[1]).IsEqualTo(Constants.AuthStatusFailure);
	}

	[Test]
	public async Task ServerReply_Succeeded_IPv4(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[Constants.MaxCommandLength];
		ServerBound bound = default;
		bound.Type = AddressType.IPv4;
		IPAddress.Loopback.TryFormat(bound.Host.WriteBuffer, out bound.Host.Length);
		bound.Port = 1080;

		int len = Pack.ServerReply(Socks5Reply.Succeeded, bound, buffer);

		await Assert.That(buffer[0]).IsEqualTo(Constants.ProtocolVersion);
		await Assert.That(buffer[1]).IsEqualTo((byte)Socks5Reply.Succeeded);
		await Assert.That(buffer[2]).IsEqualTo(Constants.Rsv);
		await Assert.That(buffer[3]).IsEqualTo((byte)AddressType.IPv4);
		await Assert.That(buffer[4]).IsEqualTo((byte)127);
		await Assert.That(buffer[5]).IsEqualTo((byte)0);
		await Assert.That(buffer[6]).IsEqualTo((byte)0);
		await Assert.That(buffer[7]).IsEqualTo((byte)1);
		await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan().Slice(8))).IsEqualTo((ushort)1080);
		await Assert.That(len).IsEqualTo(10);
	}

	[Test]
	public async Task ClientCommand_ConnectDomain(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[Constants.MaxCommandLength];
		int len = Pack.ClientCommand(Command.Connect, "example.com"u8, 80, buffer);

		await Assert.That(buffer[0]).IsEqualTo(Constants.ProtocolVersion);
		await Assert.That(buffer[1]).IsEqualTo((byte)Command.Connect);
		await Assert.That(buffer[2]).IsEqualTo(Constants.Rsv);
		await Assert.That(buffer[3]).IsEqualTo((byte)AddressType.Domain);
		await Assert.That(buffer[4]).IsEqualTo((byte)11);
		await Assert.That(Encoding.ASCII.GetString(buffer.AsSpan().Slice(5, 11))).IsEqualTo("example.com");
		await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan().Slice(16))).IsEqualTo((ushort)80);
		await Assert.That(len).IsEqualTo(18);
	}

	[Test]
	public async Task Udp_Normal(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[128];
		byte[] data = "hello"u8.ToArray();
		int len = Pack.Udp(buffer, "127.0.0.1"u8, 8080, data);

		await Assert.That(buffer[0]).IsEqualTo(Constants.Rsv);
		await Assert.That(buffer[1]).IsEqualTo(Constants.Rsv);
		await Assert.That(buffer[2]).IsEqualTo((byte)0x00); // fragment
		await Assert.That(buffer[3]).IsEqualTo((byte)AddressType.IPv4);
		await Assert.That(buffer[4]).IsEqualTo((byte)127);
		await Assert.That(buffer[5]).IsEqualTo((byte)0);
		await Assert.That(buffer[6]).IsEqualTo((byte)0);
		await Assert.That(buffer[7]).IsEqualTo((byte)1);
		await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan().Slice(8))).IsEqualTo((ushort)8080);
		await Assert.That(buffer.AsSpan().Slice(10, 5).SequenceEqual("hello"u8)).IsTrue();
		await Assert.That(len).IsEqualTo(15);
	}

}
