using Pipelines.Extensions;
using Proxy.Abstractions;
using Socks5;
using Socks5.Protocol;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using UnitTest.TestBase;

namespace UnitTest;

[Timeout(5_000)]
public class Socks5InboundTest
{
	private sealed record Fixture(
		MockHttpServer MockHttp,
		TcpListener ProxyListener,
		TcpListener AuthProxyListener,
		CancellationTokenSource Cts,
		ushort ProxyPort);

	private static Fixture? _fixture;

	private static Fixture F => _fixture ?? throw new InvalidOperationException();

	[Before(Class)]
	public static void Setup(CancellationToken cancellationToken)
	{
		MockHttpServer mockHttp = new();
		mockHttp.Start();
		CancellationTokenSource cts = new();
		DirectOutbound outbound = new();

		Socks5Inbound forwarder = new();
		TcpListener proxyListener = new(IPAddress.Loopback, 0);
		proxyListener.Start();
		_ = TestAcceptLoop.RunAsync(proxyListener, forwarder, outbound, cts.Token);
		ushort proxyPort = (ushort)((IPEndPoint)proxyListener.LocalEndpoint).Port;

		UserPassAuth cred = new()
		{
			UserName = "user"u8.ToArray(),
			Password = "pass"u8.ToArray()
		};
		Socks5Inbound authForwarder = new(cred);
		TcpListener authProxyListener = new(IPAddress.Loopback, 0);
		authProxyListener.Start();
		_ = TestAcceptLoop.RunAsync(authProxyListener, authForwarder, outbound, cts.Token);

		_fixture = new Fixture(mockHttp, proxyListener, authProxyListener, cts, proxyPort);
	}

	[After(Class)]
	public static void Cleanup(CancellationToken cancellationToken)
	{
		if (_fixture is not { } f)
		{
			return;
		}

		f.Cts.Cancel();
		f.AuthProxyListener.Stop();
		f.ProxyListener.Stop();
		f.MockHttp.Dispose();
		f.Cts.Dispose();
	}

	private static async Task NoAuthHandshakeAsync(PipeWriter toServer, PipeReader fromServer, CancellationToken cancellationToken)
	{
		await toServer.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken);
		ReadResult result = await fromServer.ReadAsync(cancellationToken);
		fromServer.AdvanceTo(result.Buffer.End);
	}

	private static InboundContext LoopbackContext(ushort localPort = 0)
	{
		return new InboundContext
		{
			ClientAddress = IPAddress.Loopback,
			ClientPort = 0,
			LocalAddress = IPAddress.Loopback,
			LocalPort = localPort,
		};
	}

	/// <summary>
	/// Sends one UDP datagram through the relay and returns how many were forwarded.
	/// Caller provides a pre-bound <paramref name="senderSocket"/> so port-aware tests
	/// can control the sender's endpoint.
	/// </summary>
	private static async Task<int> SendUdpAndCountForwardsAsync(
		byte[] dstAddr, ushort dstPort,
		Socket senderSocket, CancellationToken cancellationToken,
		InboundContext? context = null)
	{
		SpyPacketOutbound spy = new();
		Socks5Inbound inbound = new();

		Pipe clientToServer = new();
		Pipe serverToClient = new();
		IDuplexPipe pipe = DefaultDuplexPipe.Create(clientToServer.Reader, serverToClient.Writer);
		ValueTask handleTask = inbound.HandleAsync(context ?? LoopbackContext(), pipe, spy, cancellationToken);

		await NoAuthHandshakeAsync(clientToServer.Writer, serverToClient.Reader, cancellationToken);

		byte[] cmd = new byte[Constants.MaxCommandLength];
		int cmdLen = Pack.ClientCommand(Command.UdpAssociate, dstAddr, dstPort, cmd);
		await clientToServer.Writer.WriteAsync(cmd.AsMemory(0, cmdLen), cancellationToken);

		ReadResult replyResult = await serverToClient.Reader.ReadAsync(cancellationToken);
		ReadOnlySequence<byte> seq = replyResult.Buffer;
		bool parsed = Unpack.ReadServerReplyCommand(ref seq, out ServerBound bound);
		serverToClient.Reader.AdvanceTo(replyResult.Buffer.End);
		await Assert.That(parsed).IsTrue();

		byte[] payload = new byte[64];
		Random.Shared.NextBytes(payload);
		byte[] pkt = new byte[Constants.MaxUdpHandshakeHeaderLength + payload.Length];
		int pktLen = Pack.Udp(pkt, "127.0.0.1"u8, 9999, payload);
		await senderSocket.SendToAsync
		(
			pkt.AsMemory(0, pktLen), SocketFlags.None,
			new IPEndPoint(IPAddress.Loopback, bound.Port), cancellationToken
		);

		// Return promptly when forwarded; fall back to 50 ms for drop tests.
		await Task.WhenAny(spy.PacketConnection.FirstSendCompleted, Task.Delay(50, cancellationToken));

		await clientToServer.Writer.CompleteAsync();
		await handleTask;

		return spy.PacketConnection.SendToCallCount;
	}

	private static Socket CreateBoundUdpSocket(ushort port = 0)
	{
		Socket s = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
		s.Bind(new IPEndPoint(IPAddress.Loopback, port));
		return s;
	}

	[Test]
	[DisplayName("Method negotiation: desired method beyond 8th position is still accepted")]
	public async Task MethodNegotiation_DesiredMethodBeyondEighth_StillAccepted(CancellationToken cancellationToken)
	{
		Socks5Inbound inbound = new();

		Pipe clientToServer = new();
		Pipe serverToClient = new();
		IDuplexPipe pipe = DefaultDuplexPipe.Create(clientToServer.Reader, serverToClient.Writer);
		ValueTask handleTask = inbound.HandleAsync(LoopbackContext(), pipe, new SpyPacketOutbound(), cancellationToken);

		// 9 unique methods — NoAuthentication is last, beyond the old buffer[8] limit
		Method[] methods =
		[
			(Method)3, (Method)4, (Method)5, (Method)6,
			(Method)7, (Method)8, (Method)9, (Method)10,
			Method.NoAuthentication
		];
		byte[] handshake = new byte[2 + methods.Length];
		int hsLen = Pack.Handshake(methods, handshake);
		await clientToServer.Writer.WriteAsync(handshake.AsMemory(0, hsLen), cancellationToken);

		ReadResult result = await serverToClient.Reader.ReadAsync(cancellationToken);
		byte[] response = result.Buffer.ToArray();
		serverToClient.Reader.AdvanceTo(result.Buffer.End);

		await Assert.That(response[0]).IsEqualTo(Constants.ProtocolVersion);
		await Assert.That(response[1]).IsEqualTo((byte)Method.NoAuthentication);

		await clientToServer.Writer.CompleteAsync();
		await handleTask;
		await serverToClient.Writer.CompleteAsync();
	}

	[Test]
	[DisplayName("Truncated greeting (VER byte only then EOF) throws without sending 0xFF")]
	public async Task TruncatedGreeting_OnlyVer_ThrowsWithoutResponse(CancellationToken cancellationToken)
	{
		Socks5Inbound inbound = new();

		Pipe clientToServer = new();
		Pipe serverToClient = new();
		IDuplexPipe pipe = DefaultDuplexPipe.Create(clientToServer.Reader, serverToClient.Writer);
		ValueTask handleTask = inbound.HandleAsync(LoopbackContext(), pipe, new SpyPacketOutbound(), cancellationToken);

		// Send only VER byte, then EOF — incomplete greeting
		await clientToServer.Writer.WriteAsync(new byte[] { 0x05 }, cancellationToken);
		await clientToServer.Writer.CompleteAsync();

		await Assert.That(async () => await handleTask).Throws<InvalidDataException>();

		// Server must not have sent any response
		await serverToClient.Writer.CompleteAsync();
		ReadResult result = await serverToClient.Reader.ReadAsync(cancellationToken);
		await Assert.That(result.Buffer.IsEmpty).IsTrue();
	}

	[Test]
	[DisplayName("Truncated greeting (partial METHODS then EOF) throws without sending 0xFF")]
	public async Task TruncatedGreeting_PartialMethods_ThrowsWithoutResponse(CancellationToken cancellationToken)
	{
		Socks5Inbound inbound = new();

		Pipe clientToServer = new();
		Pipe serverToClient = new();
		IDuplexPipe pipe = DefaultDuplexPipe.Create(clientToServer.Reader, serverToClient.Writer);
		ValueTask handleTask = inbound.HandleAsync(LoopbackContext(), pipe, new SpyPacketOutbound(), cancellationToken);

		// VER=0x05, NMETHODS=0x02, but only 1 method byte — incomplete greeting
		await clientToServer.Writer.WriteAsync(new byte[] { 0x05, 0x02, 0x00 }, cancellationToken);
		await clientToServer.Writer.CompleteAsync();

		await Assert.That(async () => await handleTask).Throws<InvalidDataException>();

		await serverToClient.Writer.CompleteAsync();
		ReadResult result = await serverToClient.Reader.ReadAsync(cancellationToken);
		await Assert.That(result.Buffer.IsEmpty).IsTrue();
	}

	[Test]
	[DisplayName("NMETHODS=0 (malformed) closes without any response")]
	public async Task MalformedGreeting_ZeroMethods_ClosesWithoutResponse(CancellationToken cancellationToken)
	{
		Socks5Inbound inbound = new();

		Pipe clientToServer = new();
		Pipe serverToClient = new();
		IDuplexPipe pipe = DefaultDuplexPipe.Create(clientToServer.Reader, serverToClient.Writer);
		ValueTask handleTask = inbound.HandleAsync(LoopbackContext(), pipe, new SpyPacketOutbound(), cancellationToken);

		// VER=0x05, NMETHODS=0x00 — malformed greeting per RFC 1928 §3
		await clientToServer.Writer.WriteAsync(new byte[] { 0x05, 0x00 }, cancellationToken);

		await handleTask;
		await clientToServer.Writer.CompleteAsync();
		await serverToClient.Writer.CompleteAsync();

		ReadResult result = await serverToClient.Reader.ReadAsync(cancellationToken);
		await Assert.That(result.Buffer.IsEmpty).IsTrue();
	}

	[Test]
	public async Task NoAcceptableMethod_ClosesWithoutFurtherReply(CancellationToken cancellationToken)
	{
		UserPassAuth cred = new()
		{
			UserName = "u"u8.ToArray(),
			Password = "p"u8.ToArray()
		};
		Socks5Inbound inbound = new(cred);

		Pipe clientToServer = new();
		Pipe serverToClient = new();
		IDuplexPipe pipe = DefaultDuplexPipe.Create(clientToServer.Reader, serverToClient.Writer);
		ValueTask handleTask = inbound.HandleAsync(LoopbackContext(), pipe, new SpyPacketOutbound(), cancellationToken);

		// Client offers only NoAuthentication — server needs UsernamePassword
		byte[] handshake = new byte[3];
		int hsLen = Pack.Handshake([Method.NoAuthentication], handshake);
		await clientToServer.Writer.WriteAsync(handshake.AsMemory(0, hsLen), cancellationToken);

		await handleTask;
		await clientToServer.Writer.CompleteAsync();
		await serverToClient.Writer.CompleteAsync();

		ReadResult result = await serverToClient.Reader.ReadAsync(cancellationToken);
		byte[] allServerData = result.Buffer.ToArray();
		serverToClient.Reader.AdvanceTo(result.Buffer.End);

		// RFC 1928 §3: after METHOD=0xFF, server MUST close — no further reply
		await Assert.That(allServerData).Count().IsEqualTo(2);
		await Assert.That(allServerData[0]).IsEqualTo(Constants.ProtocolVersion);
		await Assert.That(allServerData[1]).IsEqualTo((byte)Method.NoAcceptable);
	}

	[Test]
	public async Task UnsupportedCommand_Bind(CancellationToken cancellationToken)
	{
		Socks5Inbound inbound = new();

		Pipe clientToServer = new();
		Pipe serverToClient = new();
		IDuplexPipe pipe = DefaultDuplexPipe.Create(clientToServer.Reader, serverToClient.Writer);
		ValueTask handleTask = inbound.HandleAsync(LoopbackContext(), pipe, new SpyPacketOutbound(), cancellationToken);

		await NoAuthHandshakeAsync(clientToServer.Writer, serverToClient.Reader, cancellationToken);

		// Send BIND command
		byte[] cmd = new byte[Constants.MaxCommandLength];
		int cmdLen = Pack.ClientCommand(Command.Bind, "127.0.0.1"u8, 80, cmd);
		await clientToServer.Writer.WriteAsync(cmd.AsMemory(0, cmdLen), cancellationToken);

		ReadResult replyResult = await serverToClient.Reader.ReadAsync(cancellationToken);
		byte[] reply = replyResult.Buffer.ToArray();
		serverToClient.Reader.AdvanceTo(replyResult.Buffer.End);

		await handleTask;

		await Assert.That(reply[0]).IsEqualTo(Constants.ProtocolVersion);
		await Assert.That(reply[1]).IsEqualTo((byte)Socks5Reply.CommandNotSupported);
	}

	[Test]
	[DisplayName("Unknown command (0x04) replies REP=0x07 CommandNotSupported (RFC 1928 §6)")]
	public async Task UnknownCommand_RepliesCommandNotSupported(CancellationToken cancellationToken)
	{
		Socks5Inbound inbound = new();

		Pipe clientToServer = new();
		Pipe serverToClient = new();
		IDuplexPipe pipe = DefaultDuplexPipe.Create(clientToServer.Reader, serverToClient.Writer);
		ValueTask handleTask = inbound.HandleAsync(LoopbackContext(), pipe, new SpyPacketOutbound(), cancellationToken);

		await NoAuthHandshakeAsync(clientToServer.Writer, serverToClient.Reader, cancellationToken);

		// Send unknown CMD=0x04 (not defined in RFC 1928)
		byte[] cmd = new byte[Constants.MaxCommandLength];
		int cmdLen = Pack.ClientCommand((Command)0x04, "127.0.0.1"u8, 80, cmd);
		await clientToServer.Writer.WriteAsync(cmd.AsMemory(0, cmdLen), cancellationToken);

		ReadResult replyResult = await serverToClient.Reader.ReadAsync(cancellationToken);
		byte[] reply = replyResult.Buffer.ToArray();
		serverToClient.Reader.AdvanceTo(replyResult.Buffer.End);

		await handleTask;

		await Assert.That(reply[0]).IsEqualTo(Constants.ProtocolVersion);
		await Assert.That(reply[1]).IsEqualTo((byte)Socks5Reply.CommandNotSupported);
	}

	[Test]
	[DisplayName("Unknown ATYP (0x05) replies REP=0x08 AddressTypeNotSupported (RFC 1928 §6)")]
	public async Task UnknownAddressType_RepliesAddressTypeNotSupported(CancellationToken cancellationToken)
	{
		Socks5Inbound inbound = new();

		Pipe clientToServer = new();
		Pipe serverToClient = new();
		IDuplexPipe pipe = DefaultDuplexPipe.Create(clientToServer.Reader, serverToClient.Writer);
		ValueTask handleTask = inbound.HandleAsync(LoopbackContext(), pipe, new SpyPacketOutbound(), cancellationToken);

		await NoAuthHandshakeAsync(clientToServer.Writer, serverToClient.Reader, cancellationToken);

		// Manually craft: VER=0x05, CMD=CONNECT, RSV=0x00, ATYP=0x05 (unknown), dummy addr+port
		await clientToServer.Writer.WriteAsync(new byte[] { 0x05, 0x01, 0x00, 0x05, 0x00, 0x00, 0x00, 0x50 }, cancellationToken);

		ReadResult replyResult = await serverToClient.Reader.ReadAsync(cancellationToken);
		byte[] reply = replyResult.Buffer.ToArray();
		serverToClient.Reader.AdvanceTo(replyResult.Buffer.End);

		await handleTask;

		await Assert.That(reply[0]).IsEqualTo(Constants.ProtocolVersion);
		await Assert.That(reply[1]).IsEqualTo((byte)Socks5Reply.AddressTypeNotSupported);
	}

	[Test]
	[DisplayName("UDP relay: DST=0.0.0.0:0 forwards from client IP with any port")]
	public async Task UdpRelay_ZeroAddr_ZeroPort_ForwardsFromClientIp(CancellationToken cancellationToken)
	{
		using Socket udp = CreateBoundUdpSocket();
		int count = await SendUdpAndCountForwardsAsync("0.0.0.0"u8.ToArray(), 0, udp, cancellationToken);
		await Assert.That(count).IsEqualTo(1);
	}

	[Test]
	[DisplayName("UDP relay: DST=127.0.0.1:0 accepts matching address")]
	public async Task UdpRelay_MatchingAddr_ZeroPort_Forwards(CancellationToken cancellationToken)
	{
		using Socket udp = CreateBoundUdpSocket();
		int count = await SendUdpAndCountForwardsAsync("127.0.0.1"u8.ToArray(), 0, udp, cancellationToken);
		await Assert.That(count).IsEqualTo(1);
	}

	[Test]
	[DisplayName("UDP relay: drops packets from non-client IP (RFC 1928 §7 MUST)")]
	public async Task UdpRelay_NonClientAddr_Drops(CancellationToken cancellationToken)
	{
		using Socket udp = CreateBoundUdpSocket();
		InboundContext nonLoopbackContext = new()
		{
			ClientAddress = IPAddress.Parse("192.0.2.1"),
			ClientPort = 0,
			LocalAddress = IPAddress.Loopback,
			LocalPort = 0,
		};
		int count = await SendUdpAndCountForwardsAsync("0.0.0.0"u8.ToArray(), 0, udp, cancellationToken, nonLoopbackContext);
		await Assert.That(count).IsEqualTo(0);
	}

	[Test]
	[DisplayName("UDP relay: DST=127.0.0.1:P accepts matching address+port")]
	public async Task UdpRelay_MatchingAddr_MatchingPort_Forwards(CancellationToken cancellationToken)
	{
		using Socket udp = CreateBoundUdpSocket();
		ushort senderPort = (ushort)((IPEndPoint)udp.LocalEndPoint!).Port;
		int count = await SendUdpAndCountForwardsAsync("127.0.0.1"u8.ToArray(), senderPort, udp, cancellationToken);
		await Assert.That(count).IsEqualTo(1);
	}

	[Test]
	[DisplayName("UDP relay: DST=127.0.0.1:P+1 drops non-matching port")]
	public async Task UdpRelay_MatchingAddr_NonMatchingPort_Drops(CancellationToken cancellationToken)
	{
		using Socket udp = CreateBoundUdpSocket();
		ushort senderPort = (ushort)((IPEndPoint)udp.LocalEndPoint!).Port;
		int count = await SendUdpAndCountForwardsAsync("127.0.0.1"u8.ToArray(), (ushort)(senderPort + 1), udp, cancellationToken);
		await Assert.That(count).IsEqualTo(0);
	}

	[Test]
	[DisplayName("UDP relay: cross-family (IPv4 client + IPv6 relay) rejects with AddressTypeNotSupported")]
	public async Task UdpRelay_CrossFamily_Rejects(CancellationToken cancellationToken)
	{
		Socks5Inbound inbound = new(udpRelayBindAddress: IPAddress.IPv6Loopback);

		Pipe clientToServer = new();
		Pipe serverToClient = new();
		IDuplexPipe pipe = DefaultDuplexPipe.Create(clientToServer.Reader, serverToClient.Writer);
		ValueTask handleTask = inbound.HandleAsync(LoopbackContext(), pipe, new SpyPacketOutbound(), cancellationToken);

		await NoAuthHandshakeAsync(clientToServer.Writer, serverToClient.Reader, cancellationToken);

		byte[] cmd = new byte[Constants.MaxCommandLength];
		int cmdLen = Pack.ClientCommand(Command.UdpAssociate, "0.0.0.0"u8.ToArray(), 0, cmd);
		await clientToServer.Writer.WriteAsync(cmd.AsMemory(0, cmdLen), cancellationToken);

		ReadResult replyResult = await serverToClient.Reader.ReadAsync(cancellationToken);
		byte[] reply = replyResult.Buffer.ToArray();
		serverToClient.Reader.AdvanceTo(replyResult.Buffer.End);

		await handleTask;

		await Assert.That(reply[0]).IsEqualTo(Constants.ProtocolVersion);
		await Assert.That(reply[1]).IsEqualTo((byte)Socks5Reply.AddressTypeNotSupported);
	}

	[Test]
	[DisplayName("UDP ASSOCIATE: default bind replies with TCP local address, not wildcard (RFC 1928 §4)")]
	public async Task UdpAssociate_DefaultBind_RepliesWithTcpLocalAddress(CancellationToken cancellationToken)
	{
		Socks5Inbound inbound = new();

		Pipe clientToServer = new();
		Pipe serverToClient = new();
		IDuplexPipe pipe = DefaultDuplexPipe.Create(clientToServer.Reader, serverToClient.Writer);
		ValueTask handleTask = inbound.HandleAsync(LoopbackContext(), pipe, new SpyPacketOutbound(), cancellationToken);

		await NoAuthHandshakeAsync(clientToServer.Writer, serverToClient.Reader, cancellationToken);

		byte[] cmd = new byte[Constants.MaxCommandLength];
		int cmdLen = Pack.ClientCommand(Command.UdpAssociate, "0.0.0.0"u8.ToArray(), 0, cmd);
		await clientToServer.Writer.WriteAsync(cmd.AsMemory(0, cmdLen), cancellationToken);

		ReadResult replyResult = await serverToClient.Reader.ReadAsync(cancellationToken);
		ReadOnlySequence<byte> seq = replyResult.Buffer;
		bool parsed = Unpack.ReadServerReplyCommand(ref seq, out ServerBound bound);
		serverToClient.Reader.AdvanceTo(replyResult.Buffer.End);

		await Assert.That(parsed).IsTrue();
		await Assert.That(bound.Host.Span.SequenceEqual("127.0.0.1"u8)).IsTrue();

		await clientToServer.Writer.CompleteAsync();
		await handleTask;
	}

	[Test]
	[DisplayName("UDP ASSOCIATE: wildcard listener replies with concrete BND.ADDR (integration)")]
	public async Task UdpAssociate_WildcardListener_RepliesWithConcreteAddress(CancellationToken cancellationToken)
	{
		Socks5Inbound inbound = new();
		DirectOutbound outbound = new();

		using TcpListener listener = new(IPAddress.Any, 0);
		listener.Start();
		ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;

		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_ = TestAcceptLoop.RunAsync(listener, inbound, outbound, cts.Token);

		using TcpClient client = new();
		await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
		NetworkStream stream = client.GetStream();

		// Method negotiation
		await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken);
		byte[] methodReply = new byte[2];
		await stream.ReadExactlyAsync(methodReply, cancellationToken);

		// UDP ASSOCIATE command
		byte[] cmd = new byte[Constants.MaxCommandLength];
		int cmdLen = Pack.ClientCommand(Command.UdpAssociate, "0.0.0.0"u8, 0, cmd);
		await stream.WriteAsync(cmd.AsMemory(0, cmdLen), cancellationToken);

		// Read reply — BND.ADDR must be concrete, not wildcard
		byte[] replyBuf = new byte[Constants.MaxCommandLength];
		int read = await stream.ReadAsync(replyBuf, cancellationToken);
		ReadOnlySequence<byte> seq = new(replyBuf.AsMemory(0, read));
		bool parsed = Unpack.ReadServerReplyCommand(ref seq, out ServerBound bound);

		await Assert.That(parsed).IsTrue();
		await Assert.That(bound.Host.Span.SequenceEqual("127.0.0.1"u8)).IsTrue();

		await cts.CancelAsync();
	}

	[Test]
	[DisplayName("CONNECT: BND.ADDR/BND.PORT match actual outbound socket (RFC 1928 §6)")]
	public async Task Connect_RepliesWithRealBoundAddress(CancellationToken cancellationToken)
	{
		await ConnectAndVerifyBoundAddress(IPAddress.Loopback, AddressType.IPv4, cancellationToken);
	}

	[Test]
	[DisplayName("CONNECT IPv6: BND.ADDR/BND.PORT match actual outbound socket (RFC 1928 §6)")]
	public async Task Connect_IPv6_RepliesWithRealBoundAddress(CancellationToken cancellationToken)
	{
		await ConnectAndVerifyBoundAddress(IPAddress.IPv6Loopback, AddressType.IPv6, cancellationToken);
	}

	private async Task ConnectAndVerifyBoundAddress(IPAddress loopback, AddressType expectedType, CancellationToken cancellationToken)
	{
		using TcpListener target = new(loopback, 0);
		target.Start();
		ushort targetPort = (ushort)((IPEndPoint)target.LocalEndpoint).Port;
		Task<Socket> acceptTask = target.AcceptSocketAsync(cancellationToken).AsTask();

		Socks5Inbound inbound = new();
		DirectOutbound outbound = new();
		using TcpListener proxy = new(loopback, 0);
		proxy.Start();
		ushort proxyPort = (ushort)((IPEndPoint)proxy.LocalEndpoint).Port;
		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_ = TestAcceptLoop.RunAsync(proxy, inbound, outbound, cts.Token);

		using TcpClient client = new();
		await client.ConnectAsync(loopback, proxyPort, cancellationToken);
		NetworkStream stream = client.GetStream();

		await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken);
		byte[] methodReply = new byte[2];
		await stream.ReadExactlyAsync(methodReply, cancellationToken);

		byte[] cmd = new byte[Constants.MaxCommandLength];
		byte[] hostBytes = System.Text.Encoding.ASCII.GetBytes(loopback.ToString());
		int cmdLen = Pack.ClientCommand(Command.Connect, hostBytes, targetPort, cmd);
		await stream.WriteAsync(cmd.AsMemory(0, cmdLen), cancellationToken);

		byte[] replyBuf = new byte[Constants.MaxCommandLength];
		int read = await stream.ReadAsync(replyBuf, cancellationToken);
		ReadOnlySequence<byte> seq = new(replyBuf.AsMemory(0, read));
		bool parsed = Unpack.ReadServerReplyCommand(ref seq, out ServerBound bound);
		await Assert.That(parsed).IsTrue();

		await Assert.That(bound.Type).IsEqualTo(expectedType);

		// The target sees the proxy's outbound socket as its RemoteEndPoint —
		// this must exactly match BND.ADDR:BND.PORT in the SOCKS5 reply.
		using Socket accepted = await acceptTask;
		IPEndPoint outboundEp = (IPEndPoint)accepted.RemoteEndPoint!;

		await Assert.That(bound.Host.Span.SequenceEqual(
			System.Text.Encoding.ASCII.GetBytes(outboundEp.Address.ToString()))).IsTrue();
		await Assert.That(bound.Port).IsEqualTo((ushort)outboundEp.Port);

		await cts.CancelAsync();
		target.Stop();
	}

	[Test]
	public async Task UdpAssociateNoAuth(CancellationToken cancellationToken)
	{
		Socks5CreateOption option = new()
		{
			Address = IPAddress.Loopback,
			Port = F.ProxyPort,
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
}
