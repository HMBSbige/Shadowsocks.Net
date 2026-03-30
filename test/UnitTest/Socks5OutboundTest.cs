using Proxy.Abstractions;
using Socks5;
using Socks5.Protocol;
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

		AuthenticationFailureException? authEx = await Assert.That
		(async () =>
			{
				await Socks5TestUtils.Socks5ConnectAsync
				(
					option, "/status/204", "localhost", (ushort)F.MockHttp.Port, cancellationToken
				);
			}
		).Throws<AuthenticationFailureException>();

		await Assert.That(authEx?.StatusCode).IsNotEqualTo((byte)0x00);
	}

	[Test]
	[DisplayName("UDP receive: drops FRAG != 0 packets (RFC 1928 §7)")]
	public async Task ReceiveFromAsync_DropsFragmentedPackets(CancellationToken cancellationToken)
	{
		// Fake SOCKS5 server: TCP for handshake, UDP for relay
		using TcpListener tcp = new(IPAddress.Loopback, 0);
		tcp.Start();
		ushort tcpPort = (ushort)((IPEndPoint)tcp.LocalEndpoint).Port;

		using Socket relaySocket = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
		relaySocket.DualMode = true;
		relaySocket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
		ushort relayPort = (ushort)((IPEndPoint)relaySocket.LocalEndPoint!).Port;

		// Run fake SOCKS5 server handshake
		_ = FakeUdpAssociateServerAsync(tcp, relayPort, cancellationToken);

		Socks5CreateOption option = new()
		{
			Address = IPAddress.Loopback,
			Port = tcpPort,
		};
		Socks5Outbound outbound = new(option);
		await using IPacketConnection pkt = await outbound.CreatePacketConnectionAsync(cancellationToken);

		// Send a probe so relay can discover outbound's UDP endpoint
		await pkt.SendToAsync("probe"u8.ToArray(), new ProxyDestination("127.0.0.1"u8.ToArray(), 9999), cancellationToken);
		byte[] probeBuf = new byte[1024];
		SocketReceiveFromResult probeResult = await relaySocket.ReceiveFromAsync
		(
			probeBuf, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), cancellationToken
		);

		// Send FRAG=1 packet (must be dropped)
		byte[] fragPkt = new byte[256];
		int fragLen = Pack.Udp(fragPkt, "10.0.0.1"u8, 8080, "dropped"u8, fragment: 1);
		await relaySocket.SendToAsync(fragPkt.AsMemory(0, fragLen), SocketFlags.None, probeResult.RemoteEndPoint, cancellationToken);

		// Send FRAG=0 packet (must be returned)
		byte[] goodPkt = new byte[256];
		byte[] expectedPayload = "hello"u8.ToArray();
		int goodLen = Pack.Udp(goodPkt, "10.0.0.1"u8, 8080, expectedPayload);
		await relaySocket.SendToAsync(goodPkt.AsMemory(0, goodLen), SocketFlags.None, probeResult.RemoteEndPoint, cancellationToken);

		byte[] resultBuf = new byte[256];
		PacketReceiveResult result = await pkt.ReceiveFromAsync(resultBuf, cancellationToken);

		await Assert.That(result.BytesReceived).IsEqualTo(expectedPayload.Length);
		await Assert.That(resultBuf.AsSpan(0, result.BytesReceived).SequenceEqual(expectedPayload)).IsTrue();
	}

	private static async Task FakeUdpAssociateServerAsync(TcpListener tcp, ushort relayPort, CancellationToken cancellationToken)
	{
		using TcpClient client = await tcp.AcceptTcpClientAsync(cancellationToken);
		NetworkStream stream = client.GetStream();
		byte[] buf = new byte[512];

		// Method negotiation: read VER+NMETHODS, then METHODS
		await stream.ReadExactlyAsync(buf.AsMemory(0, 2), cancellationToken);
		int nmethods = buf[1];
		await stream.ReadExactlyAsync(buf.AsMemory(0, nmethods), cancellationToken);
		await stream.WriteAsync(new[] { Constants.ProtocolVersion, (byte)Method.NoAuthentication }, cancellationToken);

		// UdpAssociate command: read fixed header (VER+CMD+RSV+ATYP=4), then address+port
		await stream.ReadExactlyAsync(buf.AsMemory(0, 4), cancellationToken);
		int addrLen = buf[3] switch
		{
			0x01 => 4,  // IPv4
			0x04 => 16, // IPv6
			_ => throw new InvalidOperationException()
		};
		await stream.ReadExactlyAsync(buf.AsMemory(0, addrLen + 2), cancellationToken);
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

		// Keep TCP alive until test ends
		try
		{ await Task.Delay(Timeout.Infinite, cancellationToken); }
		catch (OperationCanceledException) { }
	}

	[Test]
	public async Task AuthRequired_NoCredentials(CancellationToken cancellationToken)
	{
		Socks5CreateOption option = new()
		{
			Address = IPAddress.Loopback,
			Port = F.AuthProxyPort,
		};

		MethodUnsupportedException? methodEx = await Assert.That
		(async () =>
			{
				await Socks5TestUtils.Socks5ConnectAsync
				(
					option, "/status/204", "localhost", (ushort)F.MockHttp.Port, cancellationToken
				);
			}
		).Throws<MethodUnsupportedException>();

		await Assert.That(methodEx?.ServerReplyMethod).IsEqualTo(Method.NoAcceptable);
	}
}
