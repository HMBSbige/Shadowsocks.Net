using Socks5;
using Socks5.Protocol;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Text;
using UnitTest.TestBase;

namespace UnitTest;

public class Socks5UnpackTest
{
	// --- ReadResponseMethod ---

	[Test]
	public async Task ReadResponseMethod_NoAuth(CancellationToken cancellationToken)
	{
		byte[] data = [Constants.ProtocolVersion, (byte)Method.NoAuthentication];
		ReadOnlySequence<byte> seq = new(data);

		bool result = Unpack.ReadResponseMethod(ref seq, out Method method);

		await Assert.That(result).IsTrue();
		await Assert.That(method).IsEqualTo(Method.NoAuthentication);
		await Assert.That(seq.Length).IsEqualTo(0);
	}

	[Test]
	public async Task ReadResponseMethod_UserPassAuth(CancellationToken cancellationToken)
	{
		byte[] data = [Constants.ProtocolVersion, (byte)Method.UsernamePassword];
		ReadOnlySequence<byte> seq = new(data);

		bool result = Unpack.ReadResponseMethod(ref seq, out Method method);

		await Assert.That(result).IsTrue();
		await Assert.That(method).IsEqualTo(Method.UsernamePassword);
	}

	[Test]
	public async Task ReadResponseMethod_WrongVersion(CancellationToken cancellationToken)
	{
		byte[] data = [0x04, (byte)Method.NoAuthentication];

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			ReadOnlySequence<byte> local = new(data);
			Unpack.ReadResponseMethod(ref local, out _);
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.GeneralFailure);
	}

	[Test]
	[DisplayName("ReadResponseMethod: unknown method is a local protocol error (GeneralFailure), not ConnectionNotAllowed")]
	public async Task ReadResponseMethod_UnknownMethod(CancellationToken cancellationToken)
	{
		byte[] data = [Constants.ProtocolVersion, 0x05]; // undefined method value

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			ReadOnlySequence<byte> local = new(data);
			Unpack.ReadResponseMethod(ref local, out _);
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.GeneralFailure);
	}

	[Test]
	public async Task ReadResponseMethod_InsufficientData(CancellationToken cancellationToken)
	{
		byte[] data = [Constants.ProtocolVersion];
		ReadOnlySequence<byte> seq = new(data);

		bool result = Unpack.ReadResponseMethod(ref seq, out _);

		await Assert.That(result).IsFalse();
	}

	// --- ReadResponseAuthReply ---

	[Test]
	public async Task ReadResponseAuthReply_Success(CancellationToken cancellationToken)
	{
		byte[] data = [Constants.AuthVersion, 0x00];
		ReadOnlySequence<byte> seq = new(data);

		bool result = Unpack.ReadResponseAuthReply(ref seq);

		await Assert.That(result).IsTrue();
	}

	[Test]
	public async Task ReadResponseAuthReply_Failure(CancellationToken cancellationToken)
	{
		byte[] data = [Constants.AuthVersion, 0x01];

		Socks5AuthenticationFailureException? ex = await Assert.That(() =>
		{
			ReadOnlySequence<byte> local = new(data);
			Unpack.ReadResponseAuthReply(ref local);
		}).Throws<Socks5AuthenticationFailureException>();

		await Assert.That(ex?.StatusCode).IsEqualTo((byte)0x01);
	}

	[Test]
	[DisplayName("ReadResponseAuthReply: wrong auth version is a local protocol error (GeneralFailure), not ConnectionNotAllowed")]
	public async Task ReadResponseAuthReply_WrongVersion(CancellationToken cancellationToken)
	{
		byte[] data = [0x02, 0x00];

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			ReadOnlySequence<byte> local = new(data);
			Unpack.ReadResponseAuthReply(ref local);
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.GeneralFailure);
	}

	[Test]
	public async Task ReadResponseAuthReply_InsufficientData(CancellationToken cancellationToken)
	{
		byte[] data = [Constants.AuthVersion];
		ReadOnlySequence<byte> seq = new(data);

		bool result = Unpack.ReadResponseAuthReply(ref seq);

		await Assert.That(result).IsFalse();
	}

	// --- DestinationAddress ---

	[Test]
	public async Task DestinationAddress_IPv4(CancellationToken cancellationToken)
	{
		byte[] hostBuffer = new byte[64];
		byte[] addrBytes = [127, 0, 0, 1];

		int offset = Unpack.DestinationAddress(AddressType.IPv4, addrBytes, hostBuffer, out int written);

		await Assert.That(offset).IsEqualTo(Constants.IPv4AddressBytesLength);
		await Assert.That(written).IsGreaterThan(0);
		await Assert.That(Encoding.ASCII.GetString(hostBuffer.AsSpan(0, written))).IsEqualTo("127.0.0.1");
	}

	[Test]
	public async Task DestinationAddress_IPv6(CancellationToken cancellationToken)
	{
		byte[] hostBuffer = new byte[64];
		byte[] addrBytes = new byte[16];
		addrBytes[15] = 1; // ::1

		int offset = Unpack.DestinationAddress(AddressType.IPv6, addrBytes, hostBuffer, out int written);

		await Assert.That(offset).IsEqualTo(Constants.IPv6AddressBytesLength);
		await Assert.That(written).IsGreaterThan(0);
		await Assert.That(Encoding.ASCII.GetString(hostBuffer.AsSpan(0, written))).IsEqualTo("::1");
	}

	[Test]
	public async Task DestinationAddress_Domain(CancellationToken cancellationToken)
	{
		byte[] hostBuffer = new byte[64];
		byte[] domainBytes = "example.com"u8.ToArray();
		byte[] domainData = new byte[1 + domainBytes.Length];
		domainData[0] = (byte)domainBytes.Length;
		domainBytes.CopyTo(domainData.AsSpan(1));

		int offset = Unpack.DestinationAddress(AddressType.Domain, domainData, hostBuffer, out int written);

		// offset = domainLength + 1 (for the length byte skip)
		await Assert.That(offset).IsEqualTo(12);
		await Assert.That(written).IsEqualTo(11);
		await Assert.That(Encoding.ASCII.GetString(hostBuffer.AsSpan(0, written))).IsEqualTo("example.com");
	}

	[Test]
	[DisplayName("DestinationAddress: ATYP=Domain with length 0 rejects empty name")]
	public async Task DestinationAddress_Domain_EmptyName_Throws(CancellationToken cancellationToken)
	{
		byte[] hostBuffer = new byte[64];
		byte[] domainData = [0]; // length byte = 0

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			Unpack.DestinationAddress(AddressType.Domain, domainData, hostBuffer, out _);
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.GeneralFailure);
	}

	[Test]
	public async Task DestinationAddress_UnknownType(CancellationToken cancellationToken)
	{
		byte[] hostBuffer = new byte[64];
		byte[] data = "\0\0\0\0"u8.ToArray();

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			Unpack.DestinationAddress((AddressType)0x05, data, hostBuffer, out _);
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.AddressTypeNotSupported);
	}

	// --- ReadDestinationAddress ---

	[Test]
	public async Task ReadDestinationAddress_IPv4(CancellationToken cancellationToken)
	{
		byte[] data = [127, 0, 0, 1];
		ReadOnlySequence<byte> seq = new(data);
		SequenceReader<byte> reader = new(seq);
		byte[] hostBuffer = new byte[64];

		bool result = Unpack.ReadDestinationAddress(ref reader, AddressType.IPv4, hostBuffer, out int written);

		await Assert.That(result).IsTrue();
		await Assert.That(written).IsGreaterThan(0);
		await Assert.That(Encoding.ASCII.GetString(hostBuffer.AsSpan(0, written))).IsEqualTo("127.0.0.1");
	}

	[Test]
	public async Task ReadDestinationAddress_Domain(CancellationToken cancellationToken)
	{
		byte[] domainBytes = "example.com"u8.ToArray();
		byte[] data = new byte[1 + domainBytes.Length];
		data[0] = (byte)domainBytes.Length;
		domainBytes.CopyTo(data.AsSpan(1));
		ReadOnlySequence<byte> seq = new(data);
		SequenceReader<byte> reader = new(seq);
		byte[] hostBuffer = new byte[64];

		bool result = Unpack.ReadDestinationAddress(ref reader, AddressType.Domain, hostBuffer, out int written);

		await Assert.That(result).IsTrue();
		await Assert.That(Encoding.ASCII.GetString(hostBuffer.AsSpan(0, written))).IsEqualTo("example.com");
	}

	[Test]
	[DisplayName("ReadDestinationAddress: ATYP=Domain with length 0 rejects empty name")]
	public async Task ReadDestinationAddress_Domain_EmptyName_Throws(CancellationToken cancellationToken)
	{
		byte[] data = [0]; // domain length byte = 0

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			ReadOnlySequence<byte> seq = new(data);
			SequenceReader<byte> reader = new(seq);
			byte[] hostBuffer = new byte[64];
			Unpack.ReadDestinationAddress(ref reader, AddressType.Domain, hostBuffer, out _);
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.GeneralFailure);
	}

	[Test]
	public async Task ReadDestinationAddress_InsufficientIpData(CancellationToken cancellationToken)
	{
		byte[] data = [127, 0]; // only 2 bytes, need 4
		ReadOnlySequence<byte> seq = new(data);
		SequenceReader<byte> reader = new(seq);
		byte[] hostBuffer = new byte[64];

		bool result = Unpack.ReadDestinationAddress(ref reader, AddressType.IPv4, hostBuffer, out _);

		await Assert.That(result).IsFalse();
	}

	[Test]
	public async Task ReadDestinationAddress_InsufficientDomainData(CancellationToken cancellationToken)
	{
		byte[] data = [11, (byte)'e', (byte)'x']; // says 11 bytes, only has 2
		ReadOnlySequence<byte> seq = new(data);
		SequenceReader<byte> reader = new(seq);
		byte[] hostBuffer = new byte[64];

		bool result = Unpack.ReadDestinationAddress(ref reader, AddressType.Domain, hostBuffer, out _);
		long consumed = reader.Consumed;

		await Assert.That(result).IsFalse();
		await Assert.That(consumed).IsEqualTo(0);
	}

	// --- Udp ---

	[Test]
	public async Task Udp_Normal(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[128];
		byte[] data = "hello"u8.ToArray();
		int len = Pack.Udp(buffer, "127.0.0.1"u8, 8080, data);

		Socks5UdpReceivePacket packet = Unpack.Udp(buffer.AsMemory(0, len));

		await Assert.That(packet.Fragment).IsEqualTo((byte)0);
		await Assert.That(packet.Type).IsEqualTo(AddressType.IPv4);
		await Assert.That(packet.Port).IsEqualTo((ushort)8080);
		await Assert.That(packet.Data.Span.SequenceEqual(data)).IsTrue();
	}

	[Test]
	[DisplayName("Udp: non-zero RSV is accepted for interoperability")]
	public async Task Udp_RsvNonZero_Ignored(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[11];
		buffer[0] = 0xAB; // RSV byte 1 — non-zero
		buffer[1] = 0xCD; // RSV byte 2 — non-zero
		buffer[2] = 0x00; // FRAG
		buffer[3] = (byte)AddressType.IPv4;
		buffer[4] = 127;
		buffer[5] = 0;
		buffer[6] = 0;
		buffer[7] = 1;
		BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(8), 8080);
		buffer[10] = 0xFF; // 1 byte payload

		Socks5UdpReceivePacket packet = Unpack.Udp(buffer);

		await Assert.That(packet.Type).IsEqualTo(AddressType.IPv4);
		await Assert.That(packet.Port).IsEqualTo((ushort)8080);
		await Assert.That(packet.Data.Length).IsEqualTo(1);
	}

	[Test]
	public async Task Udp_InsufficientData(CancellationToken cancellationToken)
	{
		byte[] buffer = "\0\0\0"u8.ToArray(); // only 3 bytes, need >= 8

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			Unpack.Udp(buffer.AsMemory());
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.GeneralFailure);
	}

	[Test]
	[DisplayName("Udp: packet shorter than minimum domain length (8) is rejected at initial check")]
	public async Task Udp_ShorterThanMinimum_RejectedImmediately(CancellationToken cancellationToken)
	{
		// 5 bytes: header(4) + 1 — passes old < 4 check but not < 8
		byte[] buffer = [0x00, 0x00, 0x00, (byte)AddressType.IPv4, 127];

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			Unpack.Udp(buffer.AsMemory());
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Message).Contains("minimum 8");
	}

	[Test]
	[DisplayName("Udp: shortest valid domain packet (8 bytes, 1-char domain) parses correctly")]
	public async Task Udp_ShortestValidDomain_Parses(CancellationToken cancellationToken)
	{
		// RSV(00 00) + FRAG(00) + ATYP=Domain(03) + LEN=1(01) + "a"(61) + PORT=53(00 35)
		byte[] buffer = [0x00, 0x00, 0x00, 0x03, 0x01, 0x61, 0x00, 0x35];

		Socks5UdpReceivePacket packet = Unpack.Udp(buffer.AsMemory());

		await Assert.That(packet.Fragment).IsEqualTo((byte)0);
		await Assert.That(packet.Type).IsEqualTo(AddressType.Domain);
		await Assert.That(packet.Port).IsEqualTo((ushort)53);
		await Assert.That(Encoding.ASCII.GetString(packet.Host.Span)).IsEqualTo("a");
		await Assert.That(packet.Data.Length).IsEqualTo(0);
	}

	[Test]
	public async Task Udp_TruncatedIPv6(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[7];
		buffer[0] = 0x00; // RSV
		buffer[1] = 0x00; // RSV
		buffer[2] = 0x00; // FRAG
		buffer[3] = (byte)AddressType.IPv6; // need 16 bytes for address, only 3 available

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			Unpack.Udp(buffer.AsMemory());
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.GeneralFailure);
	}

	[Test]
	public async Task Udp_TruncatedDomain(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[7];
		buffer[0] = 0x00; // RSV
		buffer[1] = 0x00; // RSV
		buffer[2] = 0x00; // FRAG
		buffer[3] = (byte)AddressType.Domain;
		buffer[4] = 20; // domain length = 20, only 2 bytes follow

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			Unpack.Udp(buffer.AsMemory());
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.GeneralFailure);
	}

	[Test]
	public async Task Udp_TruncatedPort(CancellationToken cancellationToken)
	{
		// Valid IPv4 address but no port bytes after it
		byte[] buffer = new byte[8]; // 4 header + 4 IPv4, no room for 2-byte port
		buffer[0] = 0x00; // RSV
		buffer[1] = 0x00; // RSV
		buffer[2] = 0x00; // FRAG
		buffer[3] = (byte)AddressType.IPv4;
		buffer[4] = 127;
		buffer[5] = 0;
		buffer[6] = 0;
		buffer[7] = 1;

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			Unpack.Udp(buffer.AsMemory());
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.GeneralFailure);
	}

	[Test]
	public async Task Udp_UnknownAddressType(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[16];
		buffer[0] = 0x00; // RSV
		buffer[1] = 0x00; // RSV
		buffer[2] = 0x00; // FRAG
		buffer[3] = 0x05; // unknown ATYP

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			Unpack.Udp(buffer.AsMemory());
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.AddressTypeNotSupported);
	}

	// --- ReadServerReplyCommand ---

	[Test]
	public async Task ReadServerReplyCommand_Succeeded(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[Constants.MaxCommandLength];
		ServerBound bound = default;
		bound.Type = AddressType.IPv4;
		IPAddress.Loopback.TryFormat(bound.Host.WriteBuffer, out bound.Host.Length);
		bound.Port = 1080;
		int len = Pack.ServerReply(Socks5Reply.Succeeded, bound, buffer);

		ReadOnlySequence<byte> seq = new(buffer.AsMemory(0, len));
		bool result = Unpack.ReadServerReplyCommand(ref seq, out ServerBound parsed);

		await Assert.That(result).IsTrue();
		await Assert.That(parsed.Port).IsEqualTo((ushort)1080);
	}

	[Test]
	public async Task ReadServerReplyCommand_NonSucceeded(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[Constants.MaxCommandLength];
		ServerBound bound = default;
		bound.Type = AddressType.IPv4;
		IPAddress.Loopback.TryFormat(bound.Host.WriteBuffer, out bound.Host.Length);
		int len = Pack.ServerReply(Socks5Reply.ConnectionRefused, bound, buffer);

		byte[] data = buffer.AsSpan(0, len).ToArray();

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			ReadOnlySequence<byte> local = new(data);
			Unpack.ReadServerReplyCommand(ref local, out _);
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.ConnectionRefused);
	}

	[Test]
	[DisplayName("ReadServerReplyCommand: non-zero RSV is accepted for interoperability")]
	public async Task ReadServerReplyCommand_RsvNonZero_Ignored(CancellationToken cancellationToken)
	{
		byte[] data = new byte[10];
		data[0] = Constants.ProtocolVersion;
		data[1] = (byte)Socks5Reply.Succeeded;
		data[2] = 0xFF; // RSV — non-zero, accepted by current parser
		data[3] = (byte)AddressType.IPv4;
		data[4] = 127;
		data[5] = 0;
		data[6] = 0;
		data[7] = 1; // 127.0.0.1
		BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), 1080);

		ReadOnlySequence<byte> seq = new(data);
		bool result = Unpack.ReadServerReplyCommand(ref seq, out ServerBound bound);

		await Assert.That(result).IsTrue();
		await Assert.That(bound.Port).IsEqualTo((ushort)1080);
	}

	[Test]
	public async Task ReadServerReplyCommand_InsufficientData(CancellationToken cancellationToken)
	{
		byte[] data = [Constants.ProtocolVersion, (byte)Socks5Reply.Succeeded];
		ReadOnlySequence<byte> seq = new(data);

		bool result = Unpack.ReadServerReplyCommand(ref seq, out _);

		await Assert.That(result).IsFalse();
	}

	// --- ReadClientHandshake ---

	[Test]
	public async Task ReadClientHandshake_Normal(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[Constants.MaxHandshakeClientMethodLength];
		ReadOnlySpan<Method> methods = [Method.NoAuthentication, Method.UsernamePassword];
		int len = Pack.Handshake(methods, buffer);

		ReadOnlySequence<byte> seq = new(buffer.AsMemory(0, len));
		bool result = Unpack.ReadClientHandshake(ref seq, Method.NoAuthentication, out Method selected);

		await Assert.That(result).IsTrue();
		await Assert.That(selected).IsEqualTo(Method.NoAuthentication);
	}

	[Test]
	public async Task ReadClientHandshake_TargetNotFound(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[Constants.MaxHandshakeClientMethodLength];
		ReadOnlySpan<Method> methods = [Method.NoAuthentication];
		int len = Pack.Handshake(methods, buffer);

		ReadOnlySequence<byte> seq = new(buffer.AsMemory(0, len));
		bool result = Unpack.ReadClientHandshake(ref seq, Method.UsernamePassword, out Method selected);

		await Assert.That(result).IsTrue();
		await Assert.That(selected).IsEqualTo(Method.NoAcceptable);
	}

	[Test]
	public async Task ReadClientHandshake_WrongVersion(CancellationToken cancellationToken)
	{
		byte[] data = [0x04, 0x01, 0x00];

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			ReadOnlySequence<byte> local = new(data);
			Unpack.ReadClientHandshake(ref local, Method.NoAuthentication, out _);
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.GeneralFailure);
	}

	[Test]
	public async Task ReadClientHandshake_ZeroMethods_Throws(CancellationToken cancellationToken)
	{
		byte[] data = [Constants.ProtocolVersion, 0x00]; // NMETHODS=0

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			ReadOnlySequence<byte> local = new(data);
			Unpack.ReadClientHandshake(ref local, Method.NoAuthentication, out _);
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.GeneralFailure);
	}

	[Test]
	public async Task ReadClientHandshake_InsufficientData(CancellationToken cancellationToken)
	{
		byte[] data = [Constants.ProtocolVersion, 0x02, 0x00]; // says 2 methods, only 1
		ReadOnlySequence<byte> seq = new(data);

		bool result = Unpack.ReadClientHandshake(ref seq, Method.NoAuthentication, out _);

		await Assert.That(result).IsFalse();
	}

	// --- ReadClientAuth ---

	[Test]
	public async Task ReadClientAuth_Normal(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[Constants.MaxUsernamePasswordAuthLength];
		UserPassAuth cred = new() { UserName = "user"u8.ToArray(), Password = "pass"u8.ToArray() };
		int len = Pack.UsernamePasswordAuth(cred, buffer);

		ReadOnlySequence<byte> seq = new(buffer.AsMemory(0, len));
		UserPassAuth? parsed = null;
		bool result = Unpack.ReadClientAuth(ref seq, ref parsed);

		await Assert.That(result).IsTrue();
		await Assert.That(parsed).IsNotNull();
		UserPassAuth p = parsed.GetValueOrDefault();
		await Assert.That(p.UserName.Span.SequenceEqual("user"u8)).IsTrue();
		await Assert.That(p.Password.Span.SequenceEqual("pass"u8)).IsTrue();
	}

	[Test]
	public async Task ReadClientAuth_Unicode(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[Constants.MaxUsernamePasswordAuthLength];
		byte[] userBytes = "用户"u8.ToArray();
		byte[] passBytes = "密码"u8.ToArray();
		UserPassAuth cred = new() { UserName = userBytes, Password = passBytes };
		int len = Pack.UsernamePasswordAuth(cred, buffer);

		ReadOnlySequence<byte> seq = new(buffer.AsMemory(0, len));
		UserPassAuth? parsed = null;
		bool result = Unpack.ReadClientAuth(ref seq, ref parsed);

		await Assert.That(result).IsTrue();
		await Assert.That(parsed).IsNotNull();
		UserPassAuth p = parsed.GetValueOrDefault();
		await Assert.That(p.UserName.Span.SequenceEqual(userBytes)).IsTrue();
		await Assert.That(p.Password.Span.SequenceEqual(passBytes)).IsTrue();
	}

	[Test]
	public async Task ReadClientAuth_WrongVersion(CancellationToken cancellationToken)
	{
		byte[] data = [0x02, 0x01, (byte)'a', 0x01, (byte)'b'];

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			ReadOnlySequence<byte> local = new(data);
			UserPassAuth? p = null;
			Unpack.ReadClientAuth(ref local, ref p);
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.ConnectionNotAllowed);
	}

	[Test]
	public async Task ReadClientAuth_InsufficientData(CancellationToken cancellationToken)
	{
		byte[] data = [Constants.AuthVersion, 0x04, (byte)'u']; // says ulen=4, only 1 byte
		ReadOnlySequence<byte> seq = new(data);
		UserPassAuth? parsed = null;

		bool result = Unpack.ReadClientAuth(ref seq, ref parsed);

		await Assert.That(result).IsFalse();
	}

	[Test]
	public async Task ReadClientAuth_EmptyUsername(CancellationToken cancellationToken)
	{
		// VER=0x01, ULEN=0, PLEN=1, PASSWD='p'
		byte[] data = [Constants.AuthVersion, 0x00, 0x01, (byte)'p'];

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			ReadOnlySequence<byte> local = new(data);
			UserPassAuth? p = null;
			Unpack.ReadClientAuth(ref local, ref p);
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.ConnectionNotAllowed);
	}

	[Test]
	public async Task ReadClientAuth_EmptyPassword(CancellationToken cancellationToken)
	{
		// VER=0x01, ULEN=1, UNAME='u', PLEN=0
		byte[] data = [Constants.AuthVersion, 0x01, (byte)'u', 0x00];

		Socks5ProtocolErrorException? ex = await Assert.That(() =>
		{
			ReadOnlySequence<byte> local = new(data);
			UserPassAuth? p = null;
			Unpack.ReadClientAuth(ref local, ref p);
		}).Throws<Socks5ProtocolErrorException>();

		await Assert.That(ex?.Socks5Reply).IsEqualTo(Socks5Reply.ConnectionNotAllowed);
	}

	[Test]
	public async Task ReadClientAuth_ExpectedCredentialMatch_MultiSegment(CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[Constants.MaxUsernamePasswordAuthLength];
		UserPassAuth credential = new() { UserName = "user"u8.ToArray(), Password = "pass"u8.ToArray() };
		int len = Pack.UsernamePasswordAuth(credential, buffer);

		ReadOnlySequence<byte> seq = TestUtils.GetMultiSegmentSequence(buffer.AsMemory(0, len), 2, 7);
		bool result = Unpack.ReadClientAuth(ref seq, credential, out bool isMatch);

		await Assert.That(result).IsTrue();
		await Assert.That(isMatch).IsTrue();
		await Assert.That(seq.Length).IsEqualTo(0);
	}

	// --- Round-trip tests ---

	[Test]
	public async Task RoundTrip_AddressIPv4(CancellationToken cancellationToken)
	{
		byte[] packBuf = new byte[64];
		int packLen = Pack.DestinationAddressAndPort("192.168.1.1"u8, 8080, packBuf);

		await Assert.That(packLen).IsEqualTo(1 + Constants.IPv4AddressBytesLength + 2);

		AddressType type = (AddressType)packBuf[0];
		byte[] hostBuf = new byte[64];
		int addrLen = Unpack.DestinationAddress(type, packBuf.AsSpan().Slice(1), hostBuf, out int hostWritten);
		ushort port = BinaryPrimitives.ReadUInt16BigEndian(packBuf.AsSpan().Slice(1 + addrLen));

		await Assert.That(Encoding.ASCII.GetString(hostBuf.AsSpan(0, hostWritten))).IsEqualTo("192.168.1.1");
		await Assert.That(port).IsEqualTo((ushort)8080);
	}

	[Test]
	public async Task RoundTrip_AddressIPv6(CancellationToken cancellationToken)
	{
		byte[] packBuf = new byte[64];
		int packLen = Pack.DestinationAddressAndPort("::1"u8, 9090, packBuf);

		await Assert.That(packLen).IsEqualTo(1 + Constants.IPv6AddressBytesLength + 2);

		AddressType type = (AddressType)packBuf[0];
		byte[] hostBuf = new byte[64];
		int addrLen = Unpack.DestinationAddress(type, packBuf.AsSpan().Slice(1), hostBuf, out int hostWritten);
		ushort port = BinaryPrimitives.ReadUInt16BigEndian(packBuf.AsSpan().Slice(1 + addrLen));

		await Assert.That(Encoding.ASCII.GetString(hostBuf.AsSpan(0, hostWritten))).IsEqualTo("::1");
		await Assert.That(port).IsEqualTo((ushort)9090);
	}

	[Test]
	public async Task RoundTrip_AddressDomain(CancellationToken cancellationToken)
	{
		byte[] packBuf = new byte[64];
		int packLen = Pack.DestinationAddressAndPort("example.com"u8, 443, packBuf);

		await Assert.That(packLen).IsEqualTo(1 + 1 + "example.com"u8.Length + 2);

		AddressType type = (AddressType)packBuf[0];
		byte[] hostBuf = new byte[64];
		int addrLen = Unpack.DestinationAddress(type, packBuf.AsSpan().Slice(1), hostBuf, out int hostWritten);
		ushort port = BinaryPrimitives.ReadUInt16BigEndian(packBuf.AsSpan().Slice(1 + addrLen));

		await Assert.That(Encoding.ASCII.GetString(hostBuf.AsSpan(0, hostWritten))).IsEqualTo("example.com");
		await Assert.That(port).IsEqualTo((ushort)443);
	}

	[Test]
	public async Task RoundTrip_Udp(CancellationToken cancellationToken)
	{
		byte[] packBuf = new byte[256];
		byte[] payload = [1, 2, 3, 4, 5];
		int packLen = Pack.Udp(packBuf, "10.0.0.1"u8, 9000, payload);

		Socks5UdpReceivePacket packet = Unpack.Udp(packBuf.AsMemory(0, packLen));

		await Assert.That(packet.Port).IsEqualTo((ushort)9000);
		await Assert.That(packet.Data.Span.SequenceEqual(payload)).IsTrue();
		await Assert.That(Encoding.ASCII.GetString(packet.Host.Span)).IsEqualTo("10.0.0.1");
	}

	[Test]
	public async Task RoundTrip_ClientHandshake(CancellationToken cancellationToken)
	{
		byte[] packBuf = new byte[Constants.MaxHandshakeClientMethodLength];
		Method[] original = [Method.NoAuthentication, Method.UsernamePassword];
		int packLen = Pack.Handshake(original, packBuf);

		ReadOnlySequence<byte> seq = new(packBuf.AsMemory(0, packLen));
		Unpack.ReadClientHandshake(ref seq, Method.UsernamePassword, out Method selected);

		await Assert.That(selected).IsEqualTo(Method.UsernamePassword);
	}

	[Test]
	public async Task RoundTrip_UsernamePasswordAuth(CancellationToken cancellationToken)
	{
		byte[] packBuf = new byte[Constants.MaxUsernamePasswordAuthLength];
		UserPassAuth original = new() { UserName = "hello"u8.ToArray(), Password = "world"u8.ToArray() };
		int packLen = Pack.UsernamePasswordAuth(original, packBuf);

		ReadOnlySequence<byte> seq = new(packBuf.AsMemory(0, packLen));
		UserPassAuth? parsed = null;
		Unpack.ReadClientAuth(ref seq, ref parsed);

		await Assert.That(parsed).IsEqualTo(original);
	}
}
