using HttpProxy;
using Socks5.Models;
using Socks5.Servers;
using Socks5.Utils;
using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

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
	public async Task ConnectAsync(CancellationToken cancellationToken)
	{
		string httpsStr = await F.HttpClient.GetStringAsync($"https://localhost:{F.MockHttps.Port}/get", cancellationToken);
		await Assert.That(string.IsNullOrWhiteSpace(httpsStr)).IsFalse();
	}

	[Test]
	public async Task ConnectIPv6Async(CancellationToken cancellationToken)
	{
		string httpsStr = await F.HttpClient.GetStringAsync($"https://[::1]:{F.MockHttps.Port}/get", cancellationToken);
		await Assert.That(string.IsNullOrWhiteSpace(httpsStr)).IsFalse();
	}

	[Test]
	public async Task HttpChunkAsync(CancellationToken cancellationToken)
	{
		byte[] bytes = await F.HttpClient.GetByteArrayAsync($"http://localhost:{F.MockHttp.Port}/stream-bytes/1024", cancellationToken);
		await Assert.That(bytes.Length).IsEqualTo(1024);
	}

	[Test]
	public async Task HttpContentLengthAsync(CancellationToken cancellationToken)
	{
		string httpStr = await F.HttpClient.GetStringAsync($"http://localhost:{F.MockHttp.Port}/get", cancellationToken);
		await Assert.That(string.IsNullOrWhiteSpace(httpStr)).IsFalse();
	}

	[Test]
	public async Task HttpContentLengthIPv6Async(CancellationToken cancellationToken)
	{
		string httpStr = await F.HttpClient.GetStringAsync($"http://[::1]:{F.MockHttp.Port}/get", cancellationToken);
		await Assert.That(string.IsNullOrWhiteSpace(httpStr)).IsFalse();
	}

	[Test]
	public async Task HttpDuplicateResponseHeadersAsync(CancellationToken cancellationToken)
	{
		HttpResponseMessage response = await F.HttpClient.GetAsync($"http://localhost:{F.MockHttp.Port}/set-cookies", cancellationToken);
		string[] cookies = response.Headers.GetValues("Set-Cookie").ToArray();
		await Assert.That(cookies.Length).IsEqualTo(2);
	}

	[Test]
	public async Task HttpNoBodyAsync(CancellationToken cancellationToken)
	{
		HttpResponseMessage response = await F.HttpClient.GetAsync($"http://localhost:{F.MockHttp.Port}/status/204", cancellationToken);
		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
	}

	[Test]
	public async Task HttpChunkedUploadAsync(CancellationToken cancellationToken)
	{
		byte[] payload = "Hello, chunked world!"u8.ToArray();
		string requestHeaders = $"POST http://localhost:{F.MockHttp.Port}/echo HTTP/1.1\r\n"
							  + $"Host: localhost:{F.MockHttp.Port}\r\n"
							  + "Transfer-Encoding: chunked\r\n"
							  + "\r\n";

		string response = await SendChunkedRequestAsync(requestHeaders, payload, cancellationToken);
		await Assert.That(response.Contains("Hello, chunked world!")).IsTrue();
	}

	[Test]
	public async Task HttpCloseDelimitedAsync(CancellationToken cancellationToken)
	{
		string body = await F.HttpClient.GetStringAsync($"http://localhost:{F.MockHttp.Port}/close-delimited", cancellationToken);
		await Assert.That(body).IsEqualTo("""{"message":"close-delimited"}""");
	}

	[Test]
	public async Task HttpChunkExtensionAsync(CancellationToken cancellationToken)
	{
		byte[] bytes = await F.HttpClient.GetByteArrayAsync($"http://localhost:{F.MockHttp.Port}/stream-bytes-ext/1024", cancellationToken);
		await Assert.That(bytes.Length).IsEqualTo(1024);
	}

	[Test]
	public async Task HttpChunkTrailerAsync(CancellationToken cancellationToken)
	{
		byte[] bytes = await F.HttpClient.GetByteArrayAsync($"http://localhost:{F.MockHttp.Port}/stream-bytes-trailer/1024", cancellationToken);
		await Assert.That(bytes.Length).IsEqualTo(1024);
	}

	[Test]
	public async Task ForwardToSocks5Async(CancellationToken cancellationToken)
	{
		await Assert.That(await Socks5TestUtils.Socks5ConnectAsync(
			F.ForwardSocks5Option,
			target: "/status/204",
			targetHost: "localhost",
			targetPort: (ushort)F.MockHttp.Port,
			cancellationToken: cancellationToken
		)).IsTrue();
	}

	[Test]
	public async Task HttpContentLengthAndChunkedConflictAsync(CancellationToken cancellationToken)
	{
		byte[] payload = "Hello, chunked world!"u8.ToArray();
		// Send a POST with BOTH Content-Length and Transfer-Encoding: chunked (RFC 7230 §3.3.3: chunked wins)
		string requestHeaders = $"POST http://localhost:{F.MockHttp.Port}/echo HTTP/1.1\r\n"
							  + $"Host: localhost:{F.MockHttp.Port}\r\n"
							  + "Content-Length: 999\r\n"
							  + "Transfer-Encoding: chunked\r\n"
							  + "\r\n";

		string response = await SendChunkedRequestAsync(requestHeaders, payload, cancellationToken);
		await Assert.That(response.Contains("Hello, chunked world!")).IsTrue();
	}

	[Test]
	public async Task HttpMultiValueTransferEncodingAsync(CancellationToken cancellationToken)
	{
		byte[] payload = "Hello, chunked world!"u8.ToArray();
		// Send a POST with multi-value Transfer-Encoding (last token is chunked)
		string requestHeaders = $"POST http://localhost:{F.MockHttp.Port}/echo HTTP/1.1\r\n"
							  + $"Host: localhost:{F.MockHttp.Port}\r\n"
							  + "Transfer-Encoding: gzip, chunked\r\n"
							  + "\r\n";

		string response = await SendChunkedRequestAsync(requestHeaders, payload, cancellationToken);
		await Assert.That(response.Contains("Hello, chunked world!")).IsTrue();
	}

	[Test]
	public async Task HttpSplitTransferEncodingHeaderPreservedAsync(CancellationToken cancellationToken)
	{
		// RFC 7230 §3.2.2: multiple header lines with the same name are equivalent to
		// a single comma-separated line. "TE: gzip" + "TE: chunked" == "TE: gzip, chunked".
		byte[] payload = "Hello, chunked world!"u8.ToArray();
		string requestHeaders = $"POST http://localhost:{F.MockHttp.Port}/echo-te HTTP/1.1\r\n"
							  + $"Host: localhost:{F.MockHttp.Port}\r\n"
							  + "Transfer-Encoding: gzip\r\n"
							  + "Transfer-Encoding: chunked\r\n"
							  + "\r\n";

		string response = await SendChunkedRequestAsync(requestHeaders, payload, cancellationToken);
		// Origin must see the combined TE value including "gzip", not just "chunked"
		await Assert.That(response.Contains("gzip")).IsTrue();
	}

	[Test]
	public async Task HttpMultiConnectionHeaderStripsNominatedAsync(CancellationToken cancellationToken)
	{
		// RFC 7230 §6.1: Connection is a comma-separated list. Multiple Connection
		// header lines should be combined; all nominated headers must be stripped.
		string request = $"GET http://localhost:{F.MockHttp.Port}/echo-headers HTTP/1.1\r\n"
					   + $"Host: localhost:{F.MockHttp.Port}\r\n"
					   + "Connection: keep-alive\r\n"
					   + "Connection: X-Secret\r\n"
					   + "X-Secret: should-be-stripped\r\n"
					   + "\r\n";

		string response = await SendRawRequestAsync(request, cancellationToken);
		// X-Secret was nominated by the second Connection line — must be stripped
		await Assert.That(response.Contains("X-Secret")).IsFalse();
	}

	[Test]
	public async Task HttpMultiValueTransferEncodingHeaderPreservedAsync(CancellationToken cancellationToken)
	{
		byte[] payload = "Hello, chunked world!"u8.ToArray();
		// Send a POST with multi-value Transfer-Encoding; origin echoes back the TE header it received
		string requestHeaders = $"POST http://localhost:{F.MockHttp.Port}/echo-te HTTP/1.1\r\n"
							  + $"Host: localhost:{F.MockHttp.Port}\r\n"
							  + "Transfer-Encoding: gzip, chunked\r\n"
							  + "\r\n";

		string response = await SendChunkedRequestAsync(requestHeaders, payload, cancellationToken);
		// Proxy must preserve the full TE value, not rewrite to just "chunked"
		await Assert.That(response.Contains("gzip, chunked")).IsTrue();
	}

	[Test]
	public async Task HttpPrefixMalformedChunkSizeAsync(CancellationToken cancellationToken)
	{
		// "1G" is parsed as chunk-size 1 by the buggy parser (reads '1', stops at 'G').
		// We send exactly 1 byte of chunk data + \r\n so the framing stays aligned for the
		// buggy parser, then a valid terminating chunk. Using /status/204 (ignores body) so
		// the origin doesn't also choke on the forwarded "1G" and mask the proxy's leniency.
		// A correct parser should reject "1G" since chunk-size = 1*HEXDIG (RFC 7230 §4.1).
		byte[] firstPayload = "Hello"u8.ToArray();

		using TcpClient tcp = new();
		NetworkStream ns = await ConnectToProxyAsync(tcp, cancellationToken);

		string requestHeaders = $"POST http://localhost:{F.MockHttp.Port}/status/204 HTTP/1.1\r\n"
							  + $"Host: localhost:{F.MockHttp.Port}\r\n"
							  + "Transfer-Encoding: chunked\r\n"
							  + "\r\n";
		await ns.WriteAsync(Encoding.UTF8.GetBytes(requestHeaders), cancellationToken);
		// First valid chunk
		await ns.WriteAsync(Encoding.UTF8.GetBytes($"{firstPayload.Length:x}\r\n"), cancellationToken);
		await ns.WriteAsync(firstPayload, cancellationToken);
		await ns.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
		// Second chunk: "1G" is invalid per RFC but buggy parser treats as size=1
		// Send 1 byte of data so framing stays aligned for the buggy parser
		await ns.WriteAsync("1G\r\n"u8.ToArray(), cancellationToken);
		await ns.WriteAsync("X"u8.ToArray(), cancellationToken);
		await ns.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
		// Terminating chunk
		await ns.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellationToken);
		await ns.FlushAsync(cancellationToken);

		string response = await ReadResponseAsync(ns, cancellationToken);
		// Proxy should reject "1G" with 500, not silently accept it as size=1
		await Assert.That(response.Contains("HTTP/1.1 500")).IsTrue();
	}

	[Test]
	public async Task HttpMalformedChunkSizeAsync(CancellationToken cancellationToken)
	{
		byte[] firstPayload = "Hello"u8.ToArray();

		using TcpClient tcp = new();
		NetworkStream ns = await ConnectToProxyAsync(tcp, cancellationToken);

		// Send a chunked POST where the second chunk has an invalid hex size "XYZ"
		string requestHeaders = $"POST http://localhost:{F.MockHttp.Port}/echo HTTP/1.1\r\n"
							  + $"Host: localhost:{F.MockHttp.Port}\r\n"
							  + "Transfer-Encoding: chunked\r\n"
							  + "\r\n";
		await ns.WriteAsync(Encoding.UTF8.GetBytes(requestHeaders), cancellationToken);
		// First valid chunk
		await ns.WriteAsync(Encoding.UTF8.GetBytes($"{firstPayload.Length:x}\r\n"), cancellationToken);
		await ns.WriteAsync(firstPayload, cancellationToken);
		await ns.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
		// Second chunk with invalid size
		await ns.WriteAsync("XYZ\r\n"u8.ToArray(), cancellationToken);
		await ns.WriteAsync("data\r\n"u8.ToArray(), cancellationToken);
		await ns.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellationToken);
		await ns.FlushAsync(cancellationToken);

		string response = await ReadResponseAsync(ns, cancellationToken);
		await Assert.That(response.Contains("HTTP/1.1 500")).IsTrue();
	}

	[Test]
	public async Task HttpNonChunkedTransferEncodingPreservedAsync(CancellationToken cancellationToken)
	{
		// Transfer-Encoding: gzip (without chunked) is valid for close-delimited responses.
		// The proxy must preserve the TE header so the client knows to decode gzip.
		string request = $"GET http://localhost:{F.MockHttp.Port}/te-gzip-response HTTP/1.1\r\n"
					   + $"Host: localhost:{F.MockHttp.Port}\r\n"
					   + "\r\n";

		string response = await SendRawRequestAsync(request, cancellationToken);
		await Assert.That(response.Contains("Transfer-Encoding: gzip", StringComparison.OrdinalIgnoreCase)).IsTrue();
	}

	[Test]
	public async Task HttpLongConnectionValueAsync(CancellationToken cancellationToken)
	{
		// Connection value > 512 bytes must not crash the proxy.
		// Build Connection header with 50 tokens (> 512 bytes total)
		StringBuilder connTokens = new();
		StringBuilder extraHeaders = new();
		for (int i = 0; i < 50; i++)
		{
			if (i > 0)
				connTokens.Append(", ");
			string name = $"X-Hdr-{i:D3}";
			connTokens.Append(name);
			extraHeaders.Append($"{name}: val\r\n");
		}

		string request = $"GET http://localhost:{F.MockHttp.Port}/echo-headers HTTP/1.1\r\n"
					   + $"Host: localhost:{F.MockHttp.Port}\r\n"
					   + $"Connection: {connTokens}\r\n"
					   + extraHeaders.ToString()
					   + "\r\n";

		string response = await SendRawRequestAsync(request, cancellationToken, 16384);
		// Should get a valid 200, not a 500 from buffer overflow
		await Assert.That(response.Contains("HTTP/1.1 200")).IsTrue();
		// All X-Hdr-NNN headers should be stripped (Connection-nominated)
		await Assert.That(response.Contains("X-Hdr-")).IsFalse();
	}

	[Test]
	public async Task HttpTransferEncodingNotEndingWithChunkedAsync(CancellationToken cancellationToken)
	{
		// RFC 7230 §3.3.1: For requests, chunked MUST be the final transfer coding.
		// "chunked, gzip" has gzip as the last token — proxy must reject.
		byte[] payload = "Hello"u8.ToArray();
		string request = $"POST http://localhost:{F.MockHttp.Port}/status/204 HTTP/1.1\r\n"
					   + $"Host: localhost:{F.MockHttp.Port}\r\n"
					   + "Transfer-Encoding: chunked, gzip\r\n"
					   + "\r\n"
					   + $"{payload.Length:x}\r\n"
					   + "Hello\r\n"
					   + "0\r\n\r\n";

		string response = await SendRawRequestAsync(request, cancellationToken);
		await Assert.That(response.Contains("HTTP/1.1 500")).IsTrue();
	}

	[Test]
	public async Task HttpConflictingContentLengthAsync(CancellationToken cancellationToken)
	{
		// RFC 7230 §3.3.3: conflicting Content-Length values MUST be rejected.
		string request = $"POST http://localhost:{F.MockHttp.Port}/echo HTTP/1.1\r\n"
					   + $"Host: localhost:{F.MockHttp.Port}\r\n"
					   + "Content-Length: 5\r\n"
					   + "Content-Length: 1\r\n"
					   + "\r\n"
					   + "ABCDE";

		string response = await SendRawRequestAsync(request, cancellationToken);
		await Assert.That(response.Contains("HTTP/1.1 500")).IsTrue();
	}

	[Test]
	public async Task HttpInvalidContentLengthFormatAsync(CancellationToken cancellationToken)
	{
		// Content-Length = 1*DIGIT. "5junk" is not valid.
		string request = $"POST http://localhost:{F.MockHttp.Port}/echo HTTP/1.1\r\n"
					   + $"Host: localhost:{F.MockHttp.Port}\r\n"
					   + "Content-Length: 5junk\r\n"
					   + "\r\n"
					   + "ABCDE";

		string response = await SendRawRequestAsync(request, cancellationToken);
		await Assert.That(response.Contains("HTTP/1.1 500")).IsTrue();
	}

	private static async Task<string> ReadResponseAsync(NetworkStream ns, CancellationToken cancellationToken, int bufferSize = 8192)
	{
		byte[] buf = new byte[bufferSize];
		int totalRead = 0;
		try
		{
			while (totalRead < buf.Length)
			{
				int n = await ns.ReadAsync(buf.AsMemory(totalRead), cancellationToken);
				if (n == 0)
					break;
				totalRead += n;
			}
		}
		catch (OperationCanceledException) { }

		return Encoding.UTF8.GetString(buf, 0, totalRead);
	}

	private static async Task<NetworkStream> ConnectToProxyAsync(TcpClient tcp, CancellationToken cancellationToken)
	{
		IPEndPoint proxyEndpoint = (IPEndPoint)F.HttpServer.TcpListener.LocalEndpoint;
		await tcp.ConnectAsync(proxyEndpoint.Address, proxyEndpoint.Port, cancellationToken);
		return tcp.GetStream();
	}

	private static async Task<string> SendRawRequestAsync(string request, CancellationToken cancellationToken, int bufferSize = 8192)
	{
		using TcpClient tcp = new();
		NetworkStream ns = await ConnectToProxyAsync(tcp, cancellationToken);
		await ns.WriteAsync(Encoding.UTF8.GetBytes(request), cancellationToken);
		await ns.FlushAsync(cancellationToken);
		return await ReadResponseAsync(ns, cancellationToken, bufferSize);
	}

	private static async Task<string> SendChunkedRequestAsync(string requestHeaders, byte[] payload, CancellationToken cancellationToken)
	{
		using TcpClient tcp = new();
		NetworkStream ns = await ConnectToProxyAsync(tcp, cancellationToken);
		await ns.WriteAsync(Encoding.UTF8.GetBytes(requestHeaders), cancellationToken);
		await ns.WriteAsync(Encoding.UTF8.GetBytes($"{payload.Length:x}\r\n"), cancellationToken);
		await ns.WriteAsync(payload, cancellationToken);
		await ns.WriteAsync("\r\n0\r\n\r\n"u8.ToArray(), cancellationToken);
		await ns.FlushAsync(cancellationToken);
		return await ReadResponseAsync(ns, cancellationToken);
	}

	[Test]
	public async Task IsHttpHeaderTest(CancellationToken cancellationToken)
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
