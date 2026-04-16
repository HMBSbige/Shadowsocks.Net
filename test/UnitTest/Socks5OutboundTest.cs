using Proxy.Abstractions;
using Socks5;
using Socks5.Protocol;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using UnitTest.TestBase;

namespace UnitTest;

[Timeout(5_000)]
public class Socks5OutboundTest
{
	private sealed record Fixture(
		MockHttpServer MockHttp,
		TcpListener ProxyListener,
		TcpListener AuthProxyListener,
		TcpListener IPv6RelayProxyListener,
		CancellationTokenSource Cts,
		ushort ProxyPort,
		ushort AuthProxyPort,
		ushort IPv6RelayProxyPort);

	private static Fixture? _fixture;

	private static Fixture F => _fixture ?? throw new InvalidOperationException();

	[Before(Class)]
	public static void Setup(CancellationToken cancellationToken)
	{
		MockHttpServer mockHttp = new();
		mockHttp.Start();

		CancellationTokenSource cts = new();
		DirectOutbound outbound = new();

		// No-auth proxy via Socks5Inbound
		Socks5Inbound noAuthInbound = new();
		TcpListener proxyListener = new(IPAddress.Loopback, 0);
		proxyListener.Start();
		_ = TestAcceptLoop.RunAsync(proxyListener, noAuthInbound, outbound, cts.Token);
		ushort proxyPort = (ushort)((IPEndPoint)proxyListener.LocalEndpoint).Port;

		// Auth proxy via Socks5Inbound
		UserPassAuth cred = new()
		{
			UserName = @"114514！"u8.ToArray(),
			Password = @"1919810￥"u8.ToArray()
		};
		Socks5Inbound authInbound = new(cred);
		TcpListener authProxyListener = new(IPAddress.Loopback, 0);
		authProxyListener.Start();
		_ = TestAcceptLoop.RunAsync(authProxyListener, authInbound, outbound, cts.Token);
		ushort authProxyPort = (ushort)((IPEndPoint)authProxyListener.LocalEndpoint).Port;

		// IPv6-relay proxy via Socks5Inbound (both TCP control and UDP relay on IPv6)
		Socks5Inbound ipv6RelayInbound = new(udpRelayBindAddress: IPAddress.IPv6Loopback);
		TcpListener ipv6RelayProxyListener = new(IPAddress.IPv6Loopback, 0);
		ipv6RelayProxyListener.Start();
		_ = TestAcceptLoop.RunAsync(ipv6RelayProxyListener, ipv6RelayInbound, outbound, cts.Token);
		ushort ipv6RelayProxyPort = (ushort)((IPEndPoint)ipv6RelayProxyListener.LocalEndpoint).Port;

		_fixture = new Fixture(mockHttp, proxyListener, authProxyListener, ipv6RelayProxyListener, cts, proxyPort, authProxyPort, ipv6RelayProxyPort);
	}

	[After(Class)]
	public static void Cleanup(CancellationToken cancellationToken)
	{
		if (_fixture is not { } f)
		{
			return;
		}

		f.Cts.Cancel();
		f.IPv6RelayProxyListener.Stop();
		f.AuthProxyListener.Stop();
		f.ProxyListener.Stop();
		f.MockHttp.Dispose();
		f.Cts.Dispose();
	}

	[Test]
	public async Task ConnectThroughProxy_Domain(CancellationToken cancellationToken)
	{
		Socks5CreateOption option = new()
		{
			Address = IPAddress.Loopback,
			Port = F.ProxyPort,
		};
		await Assert.That
		(
			await Socks5TestUtils.Socks5ConnectAsync
			(
				option, "/status/204", "localhost", (ushort)F.MockHttp.Port, cancellationToken
			)
		).IsTrue();
	}

	[Test]
	public async Task ConnectThroughProxy_WithAuth(CancellationToken cancellationToken)
	{
		Socks5CreateOption option = new()
		{
			Address = IPAddress.Loopback,
			Port = F.AuthProxyPort,
			UserPassAuth = new UserPassAuth
			{
				UserName = @"114514！"u8.ToArray(),
				Password = @"1919810￥"u8.ToArray()
			}
		};
		await Assert.That
		(
			await Socks5TestUtils.Socks5ConnectAsync
			(
				option, "/status/204", "localhost", (ushort)F.MockHttp.Port, cancellationToken
			)
		).IsTrue();
	}

	[Test]
	public async Task UdpAssociate(CancellationToken cancellationToken)
	{
		Socks5CreateOption option = new()
		{
			Address = IPAddress.Loopback,
			Port = F.AuthProxyPort,
			UserPassAuth = new UserPassAuth
			{
				UserName = @"114514！"u8.ToArray(),
				Password = @"1919810￥"u8.ToArray()
			}
		};

		using MockUdpEchoServer echo = new();
		echo.Start();

		await Assert.That
		(
			await Socks5TestUtils.Socks5UdpAssociateAsync
			(
				option,
				targetHost: IPAddress.Loopback.ToString(),
				targetPort: (ushort)echo.Port,
				cancellationToken: cancellationToken
			)
		).IsTrue();
	}

	[Test]
	public async Task UdpAssociate_IPv6Relay(CancellationToken cancellationToken)
	{
		Socks5CreateOption option = new()
		{
			Address = IPAddress.IPv6Loopback,
			Port = F.IPv6RelayProxyPort,
		};

		using MockUdpEchoServer echo = new();
		echo.Start();

		await Assert.That
		(
			await Socks5TestUtils.Socks5UdpAssociateAsync
			(
				option,
				targetHost: IPAddress.Loopback.ToString(),
				targetPort: (ushort)echo.Port,
				cancellationToken: cancellationToken
			)
		).IsTrue();
	}

	[Test]
	public async Task AuthFailure_WrongPassword(CancellationToken cancellationToken)
	{
		Socks5CreateOption option = new()
		{
			Address = IPAddress.Loopback,
			Port = F.AuthProxyPort,
			UserPassAuth = new UserPassAuth
			{
				UserName = @"114514！"u8.ToArray(),
				Password = "wrong"u8.ToArray()
			}
		};

		Socks5AuthenticationFailureException? authEx = await Assert.That
		(async () =>
			{
				await Socks5TestUtils.Socks5ConnectAsync
				(
					option, "/status/204", "localhost", (ushort)F.MockHttp.Port, cancellationToken
				);
			}
		).Throws<Socks5AuthenticationFailureException>();

		await Assert.That(authEx?.StatusCode).IsNotEqualTo((byte)0x00);
	}

	[Test]
	[DisplayName("UDP receive: drops FRAG != 0 packets (RFC 1928 §7)")]
	public async Task ReceiveFromAsync_DropsFragmentedPackets(CancellationToken cancellationToken)
	{
		await AssertBadUdpPacketIsDroppedAsync(async (relay, clientEp, ct) =>
		{
			byte[] fragPkt = new byte[256];
			int fragLen = Pack.Udp(fragPkt, "10.0.0.1"u8, 8080, "dropped"u8, fragment: 1);
			await relay.SendToAsync(fragPkt.AsMemory(0, fragLen), SocketFlags.None, clientEp, ct);
		}, cancellationToken);
	}

	[Test]
	[DisplayName("UDP receive: malformed SOCKS5 relay response is silently dropped")]
	public async Task ReceiveFromAsync_MalformedRelayResponse_DroppedAndContinues(CancellationToken cancellationToken)
	{
		await AssertBadUdpPacketIsDroppedAsync(async (relay, clientEp, ct) =>
		{
			byte[] malformedPkt = new byte[12];
			malformedPkt[3] = 0x99; // invalid ATYP, rest zero
			await relay.SendToAsync(malformedPkt, SocketFlags.None, clientEp, ct);
		}, cancellationToken);
	}

	/// <summary>
	/// Scaffolds a fake SOCKS5 UDP relay, sends a bad packet via <paramref name="sendBadPacket"/>,
	/// then a valid packet, and asserts ReceiveFromAsync returns only the valid one.
	/// </summary>
	private static async Task AssertBadUdpPacketIsDroppedAsync(
		Func<Socket, EndPoint, CancellationToken, Task> sendBadPacket,
		CancellationToken cancellationToken)
	{
		using TcpListener tcp = new(IPAddress.Loopback, 0);
		tcp.Start();
		ushort tcpPort = (ushort)((IPEndPoint)tcp.LocalEndpoint).Port;

		using Socket relaySocket = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
		relaySocket.DualMode = true;
		relaySocket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
		ushort relayPort = (ushort)((IPEndPoint)relaySocket.LocalEndPoint!).Port;

		_ = FakeUdpAssociateServerAsync(tcp, relayPort, cancellationToken);

		Socks5Outbound outbound = new(new Socks5CreateOption
		{
			Address = IPAddress.Loopback,
			Port = tcpPort,
		});
		await using IPacketConnection pkt = await outbound.CreatePacketConnectionAsync(cancellationToken);

		await pkt.SendToAsync("probe"u8.ToArray(), new ProxyDestination("127.0.0.1"u8.ToArray(), 9999), cancellationToken);
		byte[] probeBuf = new byte[1024];
		SocketReceiveFromResult probeResult = await relaySocket.ReceiveFromAsync
		(
			probeBuf, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), cancellationToken
		);

		await sendBadPacket(relaySocket, probeResult.RemoteEndPoint, cancellationToken);

		byte[] goodPkt = new byte[256];
		byte[] expectedPayload = "good"u8.ToArray();
		int goodLen = Pack.Udp(goodPkt, "10.0.0.1"u8, 8080, expectedPayload);
		await relaySocket.SendToAsync(goodPkt.AsMemory(0, goodLen), SocketFlags.None, probeResult.RemoteEndPoint, cancellationToken);

		byte[] resultBuf = new byte[256];
		PacketReceiveResult result = await pkt.ReceiveFromAsync(resultBuf, cancellationToken);

		await Assert.That(result.BytesReceived).IsEqualTo(expectedPayload.Length);
		await Assert.That(resultBuf.AsSpan(0, result.BytesReceived).SequenceEqual(expectedPayload)).IsTrue();
	}

	[Test]
	[DisplayName("UDP ASSOCIATE: sends all-zero IPv4 address on IPv4 control connection")]
	public async Task UdpAssociate_IPv4ControlConnection_SendsAllZeroIPv4Address(CancellationToken cancellationToken)
	{
		using TcpListener tcp = new(IPAddress.Loopback, 0);
		tcp.Start();
		ushort tcpPort = (ushort)((IPEndPoint)tcp.LocalEndpoint).Port;

		using Socket relaySocket = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
		relaySocket.DualMode = true;
		relaySocket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
		ushort relayPort = (ushort)((IPEndPoint)relaySocket.LocalEndPoint!).Port;

		TaskCompletionSource<(byte Atyp, byte[] Addr, ushort Port)> capturedDst = new();
		_ = FakeUdpAssociateServerAsync(tcp, relayPort, capturedDst, cancellationToken);

		Socks5Outbound outbound = new(new Socks5CreateOption
		{
			Address = IPAddress.Loopback,
			Port = tcpPort,
		});

		// This implementation uses an all-zero address in the control connection's family.
		await using IPacketConnection pkt = await outbound.CreatePacketConnectionAsync(cancellationToken);

		(byte atyp, byte[] addr, ushort port) = await capturedDst.Task;

		await Assert.That(atyp).IsEqualTo((byte)0x01);
		await Assert.That(addr.SequenceEqual(new byte[4])).IsTrue();
		await Assert.That(port).IsEqualTo((ushort)0);
	}

	[Test]
	[DisplayName("UDP ASSOCIATE: sends all-zero IPv6 address on IPv6 control connection")]
	public async Task UdpAssociate_IPv6ControlConnection_SendsAllZeroIPv6Address(CancellationToken cancellationToken)
	{
		using TcpListener tcp = new(IPAddress.IPv6Loopback, 0);
		tcp.Start();
		ushort tcpPort = (ushort)((IPEndPoint)tcp.LocalEndpoint).Port;

		using Socket relaySocket = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
		relaySocket.DualMode = true;
		relaySocket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
		ushort relayPort = (ushort)((IPEndPoint)relaySocket.LocalEndPoint!).Port;

		TaskCompletionSource<(byte Atyp, byte[] Addr, ushort Port)> capturedDst = new();
		_ = FakeUdpAssociateServerAsync(tcp, relayPort, capturedDst, cancellationToken);

		Socks5Outbound outbound = new(new Socks5CreateOption
		{
			Address = IPAddress.IPv6Loopback,
			Port = tcpPort,
		});

		// This implementation uses an all-zero address in the control connection's family.
		await using IPacketConnection pkt = await outbound.CreatePacketConnectionAsync(cancellationToken);

		(byte atyp, byte[] addr, ushort port) = await capturedDst.Task;

		await Assert.That(atyp).IsEqualTo((byte)0x04);
		await Assert.That(addr.SequenceEqual(new byte[16])).IsTrue();
		await Assert.That(port).IsEqualTo((ushort)0);
	}

	[Test]
	[DisplayName("Truncated method reply (0 bytes then EOF) throws InvalidDataException")]
	public async Task TruncatedMethodReply_ZeroBytes_ThrowsInvalidDataException(CancellationToken cancellationToken)
	{
		using TcpListener tcp = new(IPAddress.Loopback, 0);
		tcp.Start();
		ushort tcpPort = (ushort)((IPEndPoint)tcp.LocalEndpoint).Port;

		_ = FakeTruncatedMethodServerAsync(tcp, bytesToSend: 0, cancellationToken);

		Socks5Outbound outbound = new(new Socks5CreateOption
		{
			Address = IPAddress.Loopback,
			Port = tcpPort,
		});

		await Assert.That(async () =>
			await outbound.ConnectAsync(new ProxyDestination("127.0.0.1"u8.ToArray(), 80), cancellationToken)
		).Throws<InvalidDataException>();
	}

	[Test]
	[DisplayName("Truncated method reply (1 byte then EOF) throws InvalidDataException")]
	public async Task TruncatedMethodReply_OneByte_ThrowsInvalidDataException(CancellationToken cancellationToken)
	{
		using TcpListener tcp = new(IPAddress.Loopback, 0);
		tcp.Start();
		ushort tcpPort = (ushort)((IPEndPoint)tcp.LocalEndpoint).Port;

		_ = FakeTruncatedMethodServerAsync(tcp, bytesToSend: 1, cancellationToken);

		Socks5Outbound outbound = new(new Socks5CreateOption
		{
			Address = IPAddress.Loopback,
			Port = tcpPort,
		});

		await Assert.That(async () =>
			await outbound.ConnectAsync(new ProxyDestination("127.0.0.1"u8.ToArray(), 80), cancellationToken)
		).Throws<InvalidDataException>();
	}

	[Test]
	[DisplayName("Constructor: empty username in UserPassAuth throws")]
	public async Task Constructor_EmptyUsername_Throws(CancellationToken cancellationToken)
	{
		await Assert.That(() => new Socks5Outbound(new Socks5CreateOption
		{
			Address = IPAddress.Loopback,
			Port = 1,
			UserPassAuth = new UserPassAuth
			{
				UserName = Array.Empty<byte>(),
				Password = "p"u8.ToArray()
			}
		})).Throws<ArgumentException>();
	}

	[Test]
	[DisplayName("Constructor: username > 255 bytes in UserPassAuth throws")]
	public async Task Constructor_UsernameTooLong_Throws(CancellationToken cancellationToken)
	{
		await Assert.That(() => new Socks5Outbound(new Socks5CreateOption
		{
			Address = IPAddress.Loopback,
			Port = 1,
			UserPassAuth = new UserPassAuth
			{
				UserName = new byte[256],
				Password = "p"u8.ToArray()
			}
		})).Throws<ArgumentException>();
	}

	[Test]
	[DisplayName("Constructor: password > 255 bytes in UserPassAuth throws")]
	public async Task Constructor_PasswordTooLong_Throws(CancellationToken cancellationToken)
	{
		await Assert.That(() => new Socks5Outbound(new Socks5CreateOption
		{
			Address = IPAddress.Loopback,
			Port = 1,
			UserPassAuth = new UserPassAuth
			{
				UserName = "u"u8.ToArray(),
				Password = new byte[256]
			}
		})).Throws<ArgumentException>();
	}

	[Test]
	[DisplayName("Constructor: empty password in UserPassAuth throws")]
	public async Task Constructor_EmptyPassword_Throws(CancellationToken cancellationToken)
	{
		await Assert.That(() => new Socks5Outbound(new Socks5CreateOption
		{
			Address = IPAddress.Loopback,
			Port = 1,
			UserPassAuth = new UserPassAuth
			{
				UserName = "u"u8.ToArray(),
				Password = Array.Empty<byte>()
			}
		})).Throws<ArgumentException>();
	}

	[Test]
	[DisplayName("Constructor: null address throws")]
	public async Task Constructor_NullAddress_Throws(CancellationToken cancellationToken)
	{
		await Assert.That(() => new Socks5Outbound(new Socks5CreateOption
		{
			Address = null!,
			Port = 1,
		})).Throws<ArgumentNullException>();
	}

	[Test]
	public async Task AuthRequired_NoCredentials(CancellationToken cancellationToken)
	{
		Socks5CreateOption option = new()
		{
			Address = IPAddress.Loopback,
			Port = F.AuthProxyPort,
		};

		Socks5MethodUnsupportedException? methodEx = await Assert.That
		(async () =>
			{
				await Socks5TestUtils.Socks5ConnectAsync
				(
					option, "/status/204", "localhost", (ushort)F.MockHttp.Port, cancellationToken
				);
			}
		).Throws<Socks5MethodUnsupportedException>();

		await Assert.That(methodEx?.ServerReplyMethod).IsEqualTo(Method.NoAcceptable);
	}

	[Test]
	[DisplayName("Truncated auth reply (0 bytes then EOF) throws InvalidDataException")]
	public async Task TruncatedAuthReply_ZeroBytes_ThrowsInvalidDataException(CancellationToken cancellationToken)
	{
		using TcpListener tcp = new(IPAddress.Loopback, 0);
		tcp.Start();
		ushort tcpPort = (ushort)((IPEndPoint)tcp.LocalEndpoint).Port;

		_ = FakeTruncatedAuthServerAsync(tcp, bytesToSend: 0, cancellationToken);

		Socks5Outbound outbound = new(new Socks5CreateOption
		{
			Address = IPAddress.Loopback,
			Port = tcpPort,
			UserPassAuth = new UserPassAuth
			{
				UserName = "u"u8.ToArray(),
				Password = "p"u8.ToArray()
			}
		});

		await Assert.That(async () =>
			await outbound.ConnectAsync(new ProxyDestination("127.0.0.1"u8.ToArray(), 80), cancellationToken)
		).Throws<InvalidDataException>();
	}

	[Test]
	[DisplayName("Truncated auth reply (1 byte then EOF) throws InvalidDataException")]
	public async Task TruncatedAuthReply_OneByte_ThrowsInvalidDataException(CancellationToken cancellationToken)
	{
		using TcpListener tcp = new(IPAddress.Loopback, 0);
		tcp.Start();
		ushort tcpPort = (ushort)((IPEndPoint)tcp.LocalEndpoint).Port;

		_ = FakeTruncatedAuthServerAsync(tcp, bytesToSend: 1, cancellationToken);

		Socks5Outbound outbound = new(new Socks5CreateOption
		{
			Address = IPAddress.Loopback,
			Port = tcpPort,
			UserPassAuth = new UserPassAuth
			{
				UserName = "u"u8.ToArray(),
				Password = "p"u8.ToArray()
			}
		});

		await Assert.That(async () =>
			await outbound.ConnectAsync(new ProxyDestination("127.0.0.1"u8.ToArray(), 80), cancellationToken)
		).Throws<InvalidDataException>();
	}

	[Test]
	[DisplayName("Truncated command reply (0 bytes then EOF) throws InvalidDataException")]
	public async Task TruncatedCommandReply_ZeroBytes_ThrowsInvalidDataException(CancellationToken cancellationToken)
	{
		using TcpListener tcp = new(IPAddress.Loopback, 0);
		tcp.Start();
		ushort tcpPort = (ushort)((IPEndPoint)tcp.LocalEndpoint).Port;

		_ = FakeTruncatedCommandServerAsync(tcp, bytesToSend: 0, cancellationToken);

		Socks5Outbound outbound = new(new Socks5CreateOption
		{
			Address = IPAddress.Loopback,
			Port = tcpPort,
		});

		await Assert.That(async () =>
			await outbound.ConnectAsync(new ProxyDestination("127.0.0.1"u8.ToArray(), 80), cancellationToken)
		).Throws<InvalidDataException>();
	}

	[Test]
	[DisplayName("Truncated command reply (1 byte then EOF) throws InvalidDataException")]
	public async Task TruncatedCommandReply_OneByte_ThrowsInvalidDataException(CancellationToken cancellationToken)
	{
		using TcpListener tcp = new(IPAddress.Loopback, 0);
		tcp.Start();
		ushort tcpPort = (ushort)((IPEndPoint)tcp.LocalEndpoint).Port;

		_ = FakeTruncatedCommandServerAsync(tcp, bytesToSend: 1, cancellationToken);

		Socks5Outbound outbound = new(new Socks5CreateOption
		{
			Address = IPAddress.Loopback,
			Port = tcpPort,
		});

		await Assert.That(async () =>
			await outbound.ConnectAsync(new ProxyDestination("127.0.0.1"u8.ToArray(), 80), cancellationToken)
		).Throws<InvalidDataException>();
	}

	private static async Task FakeTruncatedAuthServerAsync(TcpListener tcp, int bytesToSend, CancellationToken cancellationToken)
	{
		using TcpClient client = await tcp.AcceptTcpClientAsync(cancellationToken);
		NetworkStream stream = client.GetStream();
		byte[] buf = new byte[256];

		// Read method negotiation
		await stream.ReadExactlyAsync(buf.AsMemory(0, 2), cancellationToken);
		int nmethods = buf[1];
		await stream.ReadExactlyAsync(buf.AsMemory(0, nmethods), cancellationToken);

		// Reply: accept UsernamePassword
		await stream.WriteAsync(new[] { Constants.ProtocolVersion, (byte)Method.UsernamePassword }, cancellationToken);

		// Read auth request (VER + ULEN + user + PLEN + pass)
		await stream.ReadExactlyAsync(buf.AsMemory(0, 2), cancellationToken);
		int uLen = buf[1];
		await stream.ReadExactlyAsync(buf.AsMemory(0, uLen + 1), cancellationToken);
		int pLen = buf[uLen];
		await stream.ReadExactlyAsync(buf.AsMemory(0, pLen), cancellationToken);

		// Send partial auth reply then EOF
		if (bytesToSend > 0)
		{
			byte[] reply = new byte[bytesToSend];
			reply[0] = Constants.AuthVersion;
			await stream.WriteAsync(reply.AsMemory(0, bytesToSend), cancellationToken);
		}
	}

	private static async Task FakeTruncatedCommandServerAsync(TcpListener tcp, int bytesToSend, CancellationToken cancellationToken)
	{
		using TcpClient client = await tcp.AcceptTcpClientAsync(cancellationToken);
		NetworkStream stream = client.GetStream();
		byte[] buf = new byte[256];

		// Read method negotiation
		await stream.ReadExactlyAsync(buf.AsMemory(0, 2), cancellationToken);
		int nmethods = buf[1];
		await stream.ReadExactlyAsync(buf.AsMemory(0, nmethods), cancellationToken);

		// Reply: accept NoAuthentication
		await stream.WriteAsync(new[] { Constants.ProtocolVersion, (byte)Method.NoAuthentication }, cancellationToken);

		// Read command request
		await stream.ReadExactlyAsync(buf.AsMemory(0, 4), cancellationToken);
		byte atyp = buf[3];
		int addrLen = atyp switch
		{
			0x01 => 4,
			0x04 => 16,
			_ => throw new InvalidOperationException($"Unexpected ATYP: {atyp}")
		};
		await stream.ReadExactlyAsync(buf.AsMemory(0, addrLen + 2), cancellationToken);

		// Send partial command reply then EOF
		if (bytesToSend > 0)
		{
			byte[] reply = new byte[bytesToSend];
			reply[0] = Constants.ProtocolVersion;
			await stream.WriteAsync(reply.AsMemory(0, bytesToSend), cancellationToken);
		}
	}

	private static async Task FakeTruncatedMethodServerAsync(TcpListener tcp, int bytesToSend, CancellationToken cancellationToken)
	{
		using TcpClient client = await tcp.AcceptTcpClientAsync(cancellationToken);
		NetworkStream stream = client.GetStream();
		byte[] buf = new byte[256];

		// Read method negotiation
		await stream.ReadExactlyAsync(buf.AsMemory(0, 2), cancellationToken);
		int nmethods = buf[1];
		await stream.ReadExactlyAsync(buf.AsMemory(0, nmethods), cancellationToken);

		if (bytesToSend > 0)
		{
			byte[] reply = new byte[bytesToSend];
			reply[0] = Constants.ProtocolVersion;
			await stream.WriteAsync(reply, cancellationToken);
		}
	}

	private static Task FakeUdpAssociateServerAsync(TcpListener tcp, ushort relayPort, CancellationToken cancellationToken)
	{
		return FakeUdpAssociateServerAsync(tcp, relayPort, null, cancellationToken);
	}

	private static async Task FakeUdpAssociateServerAsync(
		TcpListener tcp, ushort relayPort,
		TaskCompletionSource<(byte Atyp, byte[] Addr, ushort Port)>? capturedDst,
		CancellationToken cancellationToken)
	{
		using TcpClient client = await tcp.AcceptTcpClientAsync(cancellationToken);
		NetworkStream stream = client.GetStream();
		byte[] buf = new byte[512];

		// Method negotiation
		await stream.ReadExactlyAsync(buf.AsMemory(0, 2), cancellationToken);
		int nmethods = buf[1];
		await stream.ReadExactlyAsync(buf.AsMemory(0, nmethods), cancellationToken);
		await stream.WriteAsync(new[] { Constants.ProtocolVersion, (byte)Method.NoAuthentication }, cancellationToken);

		// Command: VER+CMD+RSV+ATYP
		await stream.ReadExactlyAsync(buf.AsMemory(0, 4), cancellationToken);
		byte atyp = buf[3];
		int addrLen = atyp switch
		{
			0x01 => 4,
			0x04 => 16,
			_ => throw new InvalidOperationException($"Unexpected ATYP: {atyp}")
		};
		await stream.ReadExactlyAsync(buf.AsMemory(0, addrLen + 2), cancellationToken);

		if (capturedDst is not null)
		{
			byte[] addr = buf.AsSpan(0, addrLen).ToArray();
			ushort port = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(addrLen, 2));
			capturedDst.SetResult((atyp, addr, port));
		}

		// Reply with relay address
		ServerBound bound = new()
		{
			Type = AddressType.IPv4,
			Port = relayPort
		};
		"127.0.0.1"u8.CopyTo(bound.Host.WriteBuffer);
		bound.Host.Length = 9;
		byte[] reply = new byte[Constants.MaxCommandLength];
		int replyLen = Pack.ServerReply(Socks5Reply.Succeeded, bound, reply);
		await stream.WriteAsync(reply.AsMemory(0, replyLen), cancellationToken);

		try
		{
			await Task.Delay(Timeout.Infinite, cancellationToken);
		}
		catch (OperationCanceledException) { }
	}
}
