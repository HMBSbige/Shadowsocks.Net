using Socks5;
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
		await Assert.That(await Socks5TestUtils.Socks5ConnectAsync(
			option, "/status/204", "localhost", (ushort)F.MockHttp.Port, cancellationToken
		)).IsTrue();
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
		await Assert.That(await Socks5TestUtils.Socks5ConnectAsync(
			option, "/status/204", "localhost", (ushort)F.MockHttp.Port, cancellationToken
		)).IsTrue();
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

		await Assert.That(await Socks5TestUtils.Socks5UdpAssociateAsync(
			option,
			targetHost: IPAddress.Loopback.ToString(),
			targetPort: (ushort)echo.Port,
			cancellationToken: cancellationToken
		)).IsTrue();
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

		await Assert.That(await Socks5TestUtils.Socks5UdpAssociateAsync(
			option,
			targetHost: IPAddress.Loopback.ToString(),
			targetPort: (ushort)echo.Port,
			cancellationToken: cancellationToken
		)).IsTrue();
	}

	[Test]
	public async Task AuthFailure_WrongPassword(CancellationToken cancellationToken)
	{
		Socks5CreateOption option = new()
		{
			Address = IPAddress.Loopback,
			Port = F.AuthProxyPort,
			UserPassAuth = new UserPassAuth { UserName = @"114514！"u8.ToArray(), Password = "wrong"u8.ToArray() }
		};

		AuthenticationFailureException? authEx = await Assert.That(async () =>
		{
			await Socks5TestUtils.Socks5ConnectAsync(
				option, "/status/204", "localhost", (ushort)F.MockHttp.Port, cancellationToken);
		}).Throws<AuthenticationFailureException>();

		await Assert.That(authEx?.StatusCode).IsNotEqualTo((byte)0x00);
	}

	[Test]
	public async Task AuthRequired_NoCredentials(CancellationToken cancellationToken)
	{
		Socks5CreateOption option = new()
		{
			Address = IPAddress.Loopback,
			Port = F.AuthProxyPort,
		};

		MethodUnsupportedException? methodEx = await Assert.That(async () =>
		{
			await Socks5TestUtils.Socks5ConnectAsync(
				option, "/status/204", "localhost", (ushort)F.MockHttp.Port, cancellationToken);
		}).Throws<MethodUnsupportedException>();

		await Assert.That(methodEx?.ServerReplyMethod).IsEqualTo(Method.NoAcceptable);
	}
}
