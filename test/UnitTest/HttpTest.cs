using HttpProxy;
using Socks5.Models;
using Socks5.Servers;
using Socks5.Utils;
using System.Buffers;
using System.Net;
using System.Net.Security;

namespace UnitTest;

public class HttpTest
{
	private sealed record Fixture(
		MockHttpServer MockHttp,
		MockHttpServer MockHttps,
		SimpleSocks5Server Socks5Server,
		HttpSocks5Service HttpServer,
		HttpClient HttpClient,
		Socks5CreateOption ForwardSocks5Option);

	private static Fixture? _fixture;

	private static Fixture F => _fixture ?? throw new InvalidOperationException();

	[Before(Class)]
	public static void Setup()
	{
		MockHttpServer mockHttp = new();
		MockHttpServer mockHttps = new() { UseTls = true };
		mockHttp.Start();
		mockHttps.Start();

		IPEndPoint serverEndpoint = new(IPAddress.Loopback, 0);
		UsernamePassword userPass = new()
		{
			UserName = @"114514！",
			Password = @"1919810￥"
		};
		SimpleSocks5Server socks5Server = new(serverEndpoint, userPass);
		_ = socks5Server.StartAsync();

		ushort socks5Port = (ushort)((IPEndPoint)socks5Server.TcpListener.LocalEndpoint).Port;
		Socks5CreateOption socks5CreateOption = new()
		{
			Address = IPAddress.Loopback,
			Port = socks5Port,
			UsernamePassword = userPass
		};
		HttpSocks5Service httpServer = new(serverEndpoint, new HttpToSocks5(), socks5CreateOption);
		_ = httpServer.StartAsync();

		IPAddress httpAddress = ((IPEndPoint)httpServer.TcpListener.LocalEndpoint).Address;
		ushort httpPort = (ushort)((IPEndPoint)httpServer.TcpListener.LocalEndpoint).Port;
		SocketsHttpHandler handler = new()
		{
			UseProxy = true,
			Proxy = new WebProxy(httpAddress.ToString(), httpPort),
			AllowAutoRedirect = false,
			SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true }
		};

		_fixture = new Fixture(
			mockHttp,
			mockHttps,
			socks5Server,
			httpServer,
			new HttpClient(handler),
			new Socks5CreateOption
			{
				Address = httpAddress,
				Port = httpPort,
				UsernamePassword = userPass
			});
	}

	[After(Class)]
	public static void Cleanup()
	{
		if (_fixture is not { } f)
		{
			return;
		}

		f.HttpClient.Dispose();
		f.HttpServer.Stop();
		f.Socks5Server.Stop();
		f.MockHttps.Dispose();
		f.MockHttp.Dispose();
	}

	[Test]
	public async Task ConnectAsync()
	{
		string httpsStr = await F.HttpClient.GetStringAsync($"https://localhost:{F.MockHttps.Port}/get");
		await Assert.That(string.IsNullOrWhiteSpace(httpsStr)).IsFalse();
	}

	[Test]
	public async Task ConnectIPv6Async()
	{
		string httpsStr = await F.HttpClient.GetStringAsync($"https://[::1]:{F.MockHttps.Port}/get");
		await Assert.That(string.IsNullOrWhiteSpace(httpsStr)).IsFalse();
	}

	[Test]
	public async Task HttpChunkAsync()
	{
		byte[] bytes = await F.HttpClient.GetByteArrayAsync($"http://localhost:{F.MockHttp.Port}/stream-bytes/1024");
		await Assert.That(bytes.Length).IsEqualTo(1024);
	}

	[Test]
	public async Task HttpContentLengthAsync()
	{
		string httpStr = await F.HttpClient.GetStringAsync($"http://localhost:{F.MockHttp.Port}/get");
		await Assert.That(string.IsNullOrWhiteSpace(httpStr)).IsFalse();
	}

	[Test]
	public async Task HttpContentLengthIPv6Async()
	{
		string httpStr = await F.HttpClient.GetStringAsync($"http://[::1]:{F.MockHttp.Port}/get");
		await Assert.That(string.IsNullOrWhiteSpace(httpStr)).IsFalse();
	}

	[Test]
	public async Task HttpDuplicateResponseHeadersAsync()
	{
		HttpResponseMessage response = await F.HttpClient.GetAsync($"http://localhost:{F.MockHttp.Port}/set-cookies");
		string[] cookies = response.Headers.GetValues("Set-Cookie").ToArray();
		await Assert.That(cookies.Length).IsEqualTo(2);
	}

	[Test]
	public async Task HttpNoBodyAsync()
	{
		HttpResponseMessage response = await F.HttpClient.GetAsync($"http://localhost:{F.MockHttp.Port}/status/204");
		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
	}

	[Test]
	public async Task ForwardToSocks5Async()
	{
		await Assert.That(await Socks5TestUtils.Socks5ConnectAsync(
			F.ForwardSocks5Option,
			target: "/status/204",
			targetHost: "localhost",
			targetPort: (ushort)F.MockHttp.Port
		)).IsTrue();
	}

	[Test]
	public async Task IsHttpHeaderTest()
	{
		ReadOnlySequence<byte> sequence0 = TestUtils.GetMultiSegmentSequence(
			"GET / HTTP/1.1"u8.ToArray(),
			"\r\nHost: ip.sb\r\n"u8.ToArray(),
			"User-Agent: curl/7.55.1\r\n"u8.ToArray(),
			"\r\n"u8.ToArray()
		);
		await Assert.That(HttpUtils.IsHttpHeader(sequence0)).IsTrue();

		ReadOnlySequence<byte> sequence1 = TestUtils.GetMultiSegmentSequence(
			"GET / HTTP/1.1"u8.ToArray(),
			"\r\nHost: ip.sb\r\n"u8.ToArray(),
			"User-Agent: curl/7.55.1\r\n"u8.ToArray()
		);
		await Assert.That(HttpUtils.IsHttpHeader(sequence1)).IsFalse();

		ReadOnlySequence<byte> sequence2 = TestUtils.GetMultiSegmentSequence(
			"\r\n"u8.ToArray(),
			"\r\n"u8.ToArray()
		);
		await Assert.That(HttpUtils.IsHttpHeader(sequence2)).IsFalse();

		ReadOnlySequence<byte> sequence3 = TestUtils.GetMultiSegmentSequence(
			"GET HTTP/1.1"u8.ToArray(),
			"\r\nHost: ip.sb\r\n"u8.ToArray(),
			"User-Agent: curl/7.55.1\r\n"u8.ToArray(),
			"\r\n"u8.ToArray()
		);
		await Assert.That(HttpUtils.IsHttpHeader(sequence3)).IsFalse();
	}
}
