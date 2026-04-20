using HttpProxy;
using Proxy.Abstractions;
using Socks5;
using System.Net;
using System.Net.Security;
using UnitTest.TestBase;
using static UnitTest.TestBase.Socks5TestUtils;

namespace UnitTest;

[Timeout(20_000)]
public class SingBoxInteropTest
{
	[Before(Test)]
	public void SkipWhenSingBoxUnavailable(CancellationToken _)
	{
		Skip.When(!SingBoxTestUtils.IsAvailable, SingBoxTestUtils.SkipReason);
	}

	[Test]
	public async Task HttpInbound_SingBoxHttpOutbound_HttpsConnectAsync(CancellationToken cancellationToken)
	{
		await AssertHttpsConnectViaSingBoxOutboundAsync(new HttpInbound(), SingBoxProtocol.Http, cancellationToken);
	}

	[Test]
	public async Task Socks5Inbound_SingBoxSocksOutbound_HttpsConnectAsync(CancellationToken cancellationToken)
	{
		await AssertHttpsConnectViaSingBoxOutboundAsync(CreateInbound(), SingBoxProtocol.Socks, cancellationToken);
	}

	[Test]
	public async Task Socks5Outbound_SingBoxSocksInbound_ConnectAsync(CancellationToken cancellationToken)
	{
		using MockHttpServer mockHttp = new();
		mockHttp.Start();
		ushort targetPort = (ushort)mockHttp.Port;

		await AssertSocks5ViaSingBoxSocksInboundAsync
		(
			(option, ct) => Socks5ConnectAsync(option, "/get", "localhost", targetPort, ct),
			cancellationToken
		);
	}

	[Test]
	public async Task Socks5Outbound_SingBoxSocksInbound_UdpAssociateAsync(CancellationToken cancellationToken)
	{
		using MockUdpEchoServer echo = new();
		echo.Start();
		ushort targetPort = (ushort)echo.Port;

		await AssertSocks5ViaSingBoxSocksInboundAsync
		(
			(option, ct) => Socks5UdpAssociateAsync(option, IPAddress.Loopback.ToString(), targetPort, ct),
			cancellationToken
		);
	}

	private static async Task AssertSocks5ViaSingBoxSocksInboundAsync(
		Func<Socks5OutboundOption, CancellationToken, ValueTask<bool>> sendRequestAsync,
		CancellationToken cancellationToken)
	{
		await using SingBoxInstance singBox = await SingBoxTestUtils.StartSocksInboundAsync(cancellationToken);
		Socks5OutboundOption proxyOptions = CreateOutboundOption(singBox.Port);
		bool requestSucceeded = await sendRequestAsync(proxyOptions, cancellationToken);

		await Assert.That(requestSucceeded).IsTrue();
	}

	private static async Task AssertHttpsConnectViaSingBoxOutboundAsync(
		IStreamInbound inbound,
		SingBoxProtocol outboundProtocol,
		CancellationToken cancellationToken)
	{
		using MockHttpServer httpsServer = new() { UseTls = true };
		httpsServer.Start();

		await using InboundHarness inboundHarness = InboundHarness.Start(inbound, cancellationToken);
		await using SingBoxInstance singBox = await SingBoxTestUtils.StartHttpInboundAsync(outboundProtocol, inboundHarness.Port, cancellationToken);

		SocketsHttpHandler handler = new()
		{
			UseProxy = true,
			Proxy = new WebProxy(IPAddress.Loopback.ToString(), singBox.Port),
			AllowAutoRedirect = false,
			SslOptions = new SslClientAuthenticationOptions
			{
				RemoteCertificateValidationCallback = (_, _, _, _) => true,
			},
		};

		using HttpMessageInvoker invoker = new(handler, disposeHandler: true);
		using HttpRequestMessage httpRequest = new(HttpMethod.Get, $"https://localhost:{httpsServer.Port}/get");
		using HttpResponseMessage httpResponse = await invoker.SendAsync(httpRequest, cancellationToken);
		httpResponse.EnsureSuccessStatusCode();

		string responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
		await Assert.That(responseBody).Contains("hello");
	}
}
