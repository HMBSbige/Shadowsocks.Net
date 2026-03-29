using Socks5;
using System.Buffers;
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

	[Test]
	public async Task UnsupportedCommand_Bind(CancellationToken cancellationToken)
	{
		// Raw TCP client: handshake then send BIND command
		using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		socket.NoDelay = true;
		await socket.ConnectAsync(IPAddress.Loopback, F.ProxyPort, cancellationToken);

		// Client handshake: VER=05, NMETHODS=1, METHODS=[00]
		await socket.SendAsync(new byte[] { 0x05, 0x01, 0x00 }, SocketFlags.None, cancellationToken);

		// Read server method selection
		byte[] methodReply = new byte[2];
		await socket.ReceiveAsync(methodReply, SocketFlags.None, cancellationToken);
		await Assert.That(methodReply[0]).IsEqualTo((byte)0x05);
		await Assert.That(methodReply[1]).IsEqualTo((byte)0x00); // NoAuth

		// Send BIND command: VER=05, CMD=02(BIND), RSV=00, ATYP=01(IPv4), ADDR=127.0.0.1, PORT=80
		byte[] bindCmd = [0x05, 0x02, 0x00, 0x01, 0x7F, 0x00, 0x00, 0x01, 0x00, 0x50];
		await socket.SendAsync(bindCmd, SocketFlags.None, cancellationToken);

		// Read server reply
		byte[] reply = new byte[32];
		int read = await socket.ReceiveAsync(reply, SocketFlags.None, cancellationToken);

		await Assert.That(read).IsGreaterThanOrEqualTo(3);
		await Assert.That(reply[0]).IsEqualTo((byte)0x05); // VER
		await Assert.That(reply[1]).IsEqualTo((byte)Socks5Reply.CommandNotSupported);
	}

	[Test]
	public async Task IsClientHeader_ValidHeader(CancellationToken cancellationToken)
	{
		byte[] data = [0x05, 0x01, 0x00]; // VER=05, NMETHODS=1, METHOD=00
		ReadOnlySequence<byte> seq = new(data);

		await Assert.That(Socks5Inbound.IsClientHeader(seq)).IsTrue();
	}

	[Test]
	public async Task IsClientHeader_WrongVersion(CancellationToken cancellationToken)
	{
		byte[] data = [0x04, 0x01, 0x00];
		ReadOnlySequence<byte> seq = new(data);

		await Assert.That(Socks5Inbound.IsClientHeader(seq)).IsFalse();
	}

	[Test]
	public async Task IsClientHeader_Empty(CancellationToken cancellationToken)
	{
		ReadOnlySequence<byte> seq = ReadOnlySequence<byte>.Empty;

		await Assert.That(Socks5Inbound.IsClientHeader(seq)).IsFalse();
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

		await Assert.That(await Socks5TestUtils.Socks5UdpAssociateAsync(
			option,
			targetHost: IPAddress.Loopback.ToString(),
			targetPort: (ushort)echo.Port,
			cancellationToken: cancellationToken
		)).IsTrue();
	}
}
