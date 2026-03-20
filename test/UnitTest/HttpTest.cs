using HttpProxy;
using Pipelines.Extensions;
using Proxy.Abstractions;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using UnitTest.TestBase;

namespace UnitTest;

[Timeout(5_000)]
public class HttpTest
{
	private sealed record Fixture(
		MockHttpServer MockHttp,
		MockHttpServer MockHttps,
		TcpListener ProxyListener,
		TcpListener AuthProxyListener,
		HttpClient HttpClient,
		HttpClient AuthHttpClient);

	private static Fixture? _fixture;
	private static CancellationTokenSource? _cts;

	private static Fixture F => _fixture ?? throw new InvalidOperationException();

	[Before(Class)]
	public static void Setup(CancellationToken cancellationToken)
	{
		MockHttpServer mockHttp = new();
		MockHttpServer mockHttps = new() { UseTls = true };
		mockHttp.Start();
		mockHttps.Start();

		_cts = new CancellationTokenSource();

		HttpForwarder forwarder = new();
		DirectOutbound outbound = new();

		TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		_ = AcceptLoopAsync(listener, forwarder, outbound, _cts.Token);

		// Auth proxy with credentials
		HttpForwarder authForwarder = new(new HttpProxyCredential("user", "pass"));
		TcpListener authListener = new(IPAddress.Loopback, 0);
		authListener.Start();
		_ = AcceptLoopAsync(authListener, authForwarder, outbound, _cts.Token);

		ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
		SocketsHttpHandler handler = new()
		{
			UseProxy = true,
			Proxy = new WebProxy(IPAddress.Loopback.ToString(), port),
			AllowAutoRedirect = false,
			SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true }
		};

		ushort authPort = (ushort)((IPEndPoint)authListener.LocalEndpoint).Port;
		SocketsHttpHandler authHandler = new()
		{
			UseProxy = true,
			Proxy = new WebProxy(IPAddress.Loopback.ToString(), authPort) { Credentials = new NetworkCredential("user", "pass") },
			AllowAutoRedirect = false,
			SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true }
		};

		_fixture = new Fixture(mockHttp, mockHttps, listener, authListener, new HttpClient(handler), new HttpClient(authHandler));
	}

	[After(Class)]
	public static void Cleanup(CancellationToken cancellationToken)
	{
		if (_fixture is not { } f)
		{
			return;
		}

		_cts?.Cancel();
		f.AuthHttpClient.Dispose();
		f.HttpClient.Dispose();
		f.AuthProxyListener.Stop();
		f.ProxyListener.Stop();
		f.MockHttps.Dispose();
		f.MockHttp.Dispose();
		_cts?.Dispose();
	}

	[Test]
	public async Task ConnectAsync(CancellationToken cancellationToken)
	{
		string httpsStr = await F.HttpClient.GetStringAsync($"https://localhost:{F.MockHttps.Port}/get", cancellationToken);
		await Assert.That(httpsStr).IsNotNullOrWhiteSpace();
	}

	[Test]
	public async Task ConnectIPv6Async(CancellationToken cancellationToken)
	{
		string httpsStr = await F.HttpClient.GetStringAsync($"https://[::1]:{F.MockHttps.Port}/get", cancellationToken);
		await Assert.That(httpsStr).IsNotNullOrWhiteSpace();
	}

	[Test]
	public async Task HttpChunkAsync(CancellationToken cancellationToken)
	{
		const int chunkSize = 1023;
		byte[] bytes = await F.HttpClient.GetByteArrayAsync($"http://localhost:{F.MockHttp.Port}/stream-bytes/{chunkSize}", cancellationToken);
		await Assert.That(bytes.Length).IsEqualTo(chunkSize);
	}

	[Test]
	public async Task HttpContentLengthAsync(CancellationToken cancellationToken)
	{
		string httpStr = await F.HttpClient.GetStringAsync($"http://localhost:{F.MockHttp.Port}/get", cancellationToken);
		await Assert.That(httpStr).IsNotNullOrWhiteSpace();
	}

	[Test]
	public async Task HttpContentLengthIPv6Async(CancellationToken cancellationToken)
	{
		string httpStr = await F.HttpClient.GetStringAsync($"http://[::1]:{F.MockHttp.Port}/get", cancellationToken);
		await Assert.That(httpStr).IsNotNullOrWhiteSpace();
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
		await Assert.That(await response.Content.ReadAsStringAsync(cancellationToken)).IsEqualTo(string.Empty);
	}

	[Test]
	public async Task HttpChunkedUploadAsync(CancellationToken cancellationToken)
	{
		HttpRequestMessage request = new(HttpMethod.Post, $"http://localhost:{F.MockHttp.Port}/echo");
		request.Content = new ByteArrayContent("Hello, chunked world!"u8.ToArray());
		request.Headers.TransferEncodingChunked = true;
		HttpResponseMessage response = await F.HttpClient.SendAsync(request, cancellationToken);
		string body = await response.Content.ReadAsStringAsync(cancellationToken);
		await Assert.That(body).Contains("Hello, chunked world!");
	}

	[Test]
	public async Task HttpContentLengthUploadAsync(CancellationToken cancellationToken)
	{
		StringContent content = new("Hello, fixed-length world!");
		HttpResponseMessage response = await F.HttpClient.PostAsync($"http://localhost:{F.MockHttp.Port}/echo", content, cancellationToken);
		string body = await response.Content.ReadAsStringAsync(cancellationToken);
		await Assert.That(body).Contains("Hello, fixed-length world!");
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
		const int chunkSize = 1023;
		byte[] bytes = await F.HttpClient.GetByteArrayAsync($"http://localhost:{F.MockHttp.Port}/stream-bytes-ext/{chunkSize}", cancellationToken);
		await Assert.That(bytes.Length).IsEqualTo(chunkSize);
	}

	[Test]
	public async Task HttpChunkTrailerAsync(CancellationToken cancellationToken)
	{
		const int chunkSize = 1023;
		byte[] bytes = await F.HttpClient.GetByteArrayAsync($"http://localhost:{F.MockHttp.Port}/stream-bytes-trailer/{chunkSize}", cancellationToken);
		await Assert.That(bytes.Length).IsEqualTo(chunkSize);
	}

	[Test]
	public async Task HttpContentLengthAndChunkedConflictAsync(CancellationToken cancellationToken)
	{
		byte[] payload = "Hello, chunked world!"u8.ToArray();
		// Send a POST with BOTH Content-Length and Transfer-Encoding: chunked (RFC 9112 §6.3: chunked wins)
		string requestHeaders = $"POST http://localhost:{F.MockHttp.Port}/echo HTTP/1.1\r\n"
								+ $"Host: localhost:{F.MockHttp.Port}\r\n"
								+ "Content-Length: 999\r\n"
								+ "Transfer-Encoding: chunked\r\n"
								+ "\r\n";

		string response = await SendChunkedRequestAsync(requestHeaders, payload, cancellationToken);
		await Assert.That(response).Contains("Hello, chunked world!");
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
		await Assert.That(response).Contains("Hello, chunked world!");
	}

	[Test]
	public async Task HttpSplitTransferEncodingHeaderPreservedAsync(CancellationToken cancellationToken)
	{
		// RFC 9110 §5.3: multiple header lines with the same name are equivalent to
		// a single comma-separated line. "TE: gzip" + "TE: chunked" == "TE: gzip, chunked".
		byte[] payload = "Hello, chunked world!"u8.ToArray();
		string requestHeaders = $"POST http://localhost:{F.MockHttp.Port}/echo-te HTTP/1.1\r\n"
								+ $"Host: localhost:{F.MockHttp.Port}\r\n"
								+ "Transfer-Encoding: gzip\r\n"
								+ "Transfer-Encoding: chunked\r\n"
								+ "\r\n";

		string response = await SendChunkedRequestAsync(requestHeaders, payload, cancellationToken);
		// Origin must see the combined TE value including "gzip", not just "chunked"
		await Assert.That(response).Contains("gzip");
	}

	[Test]
	public async Task HttpMultiConnectionHeaderStripsNominatedAsync(CancellationToken cancellationToken)
	{
		// RFC 9110 §7.6.1: Connection is a comma-separated list. Multiple Connection
		// header lines should be combined; all nominated headers must be stripped.
		string request = $"GET http://localhost:{F.MockHttp.Port}/echo-headers HTTP/1.1\r\n"
						+ $"Host: localhost:{F.MockHttp.Port}\r\n"
						+ "Connection: keep-alive\r\n"
						+ "Connection: X-Secret\r\n"
						+ "X-Secret: should-be-stripped\r\n"
						+ "\r\n";

		string response = await SendRawRequestAsync(request, cancellationToken);
		// X-Secret was nominated by the second Connection line — must be stripped
		await Assert.That(response).DoesNotContain("X-Secret");
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
		await Assert.That(response).Contains("gzip, chunked");
	}

	[Test]
	public async Task HttpPrefixMalformedChunkSizeAsync(CancellationToken cancellationToken)
	{
		// "1G" is parsed as chunk-size 1 by the buggy parser (reads '1', stops at 'G').
		// We send exactly 1 byte of chunk data + \r\n so the framing stays aligned for the
		// buggy parser, then a valid terminating chunk. Using /status/204 (ignores body) so
		// the origin doesn't also choke on the forwarded "1G" and mask the proxy's leniency.
		// A correct parser should reject "1G" since chunk-size = 1*HEXDIG (RFC 9112 §7.1).
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
		await Assert.That(response).Contains("HTTP/1.1 500");
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
		await Assert.That(response).Contains("HTTP/1.1 500");
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
		await Assert.That(response).Contains("Transfer-Encoding: gzip", StringComparison.OrdinalIgnoreCase);
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
			{
				connTokens.Append(", ");
			}

			string name = $"X-Hdr-{i:D3}";
			connTokens.Append(name);
			extraHeaders.Append($"{name}: val\r\n");
		}

		string request = $"GET http://localhost:{F.MockHttp.Port}/echo-headers HTTP/1.1\r\n"
						+ $"Host: localhost:{F.MockHttp.Port}\r\n"
						+ $"Connection: {connTokens}\r\n"
						+ extraHeaders
						+ "\r\n";

		string response = await SendRawRequestAsync(request, cancellationToken, 16384);
		// Should get a valid 200, not a 500 from buffer overflow
		await Assert.That(response).Contains("HTTP/1.1 200");
		// All X-Hdr-NNN headers should be stripped (Connection-nominated)
		await Assert.That(response).DoesNotContain("X-Hdr-");
	}

	[Test]
	public async Task HttpTransferEncodingNotEndingWithChunkedAsync(CancellationToken cancellationToken)
	{
		// RFC 9112 §6.1: For requests, chunked MUST be the final transfer coding.
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
		await Assert.That(response).Contains("HTTP/1.1 500");
	}

	[Test]
	public async Task HttpConflictingContentLengthAsync(CancellationToken cancellationToken)
	{
		// RFC 9112 §6.3: conflicting Content-Length values MUST be rejected.
		string request = $"POST http://localhost:{F.MockHttp.Port}/echo HTTP/1.1\r\n"
						+ $"Host: localhost:{F.MockHttp.Port}\r\n"
						+ "Content-Length: 5\r\n"
						+ "Content-Length: 1\r\n"
						+ "\r\n"
						+ "ABCDE";

		string response = await SendRawRequestAsync(request, cancellationToken);
		await Assert.That(response).Contains("HTTP/1.1 500");
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
		await Assert.That(response).Contains("HTTP/1.1 500");
	}

	[Test]
	public async Task AuthProxy_NoCredentials_Returns407Async(CancellationToken cancellationToken)
	{
		ushort authPort = (ushort)((IPEndPoint)F.AuthProxyListener.LocalEndpoint).Port;
		using HttpClient client = new(new SocketsHttpHandler
		{
			UseProxy = true,
			Proxy = new WebProxy(IPAddress.Loopback.ToString(), authPort)
		});
		HttpResponseMessage response = await client.GetAsync($"http://localhost:{F.MockHttp.Port}/get", cancellationToken);
		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ProxyAuthenticationRequired);
	}

	[Test]
	public async Task AuthProxy_WrongCredentials_Returns407Async(CancellationToken cancellationToken)
	{
		ushort authPort = (ushort)((IPEndPoint)F.AuthProxyListener.LocalEndpoint).Port;
		using HttpClient client = new(new SocketsHttpHandler
		{
			UseProxy = true,
			Proxy = new WebProxy(IPAddress.Loopback.ToString(), authPort) { Credentials = new NetworkCredential("wrong", "creds") }
		});
		HttpResponseMessage response = await client.GetAsync($"http://localhost:{F.MockHttp.Port}/get", cancellationToken);
		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ProxyAuthenticationRequired);
	}

	[Test]
	public async Task AuthProxy_CorrectCredentials_ForwardsAsync(CancellationToken cancellationToken)
	{
		string httpStr = await F.AuthHttpClient.GetStringAsync($"http://localhost:{F.MockHttp.Port}/get", cancellationToken);
		await Assert.That(httpStr).IsNotNullOrWhiteSpace();
	}

	[Test]
	public async Task AuthProxy_Connect_CorrectCredentialsAsync(CancellationToken cancellationToken)
	{
		string httpsStr = await F.AuthHttpClient.GetStringAsync($"https://localhost:{F.MockHttps.Port}/get", cancellationToken);
		await Assert.That(httpsStr).IsNotNullOrWhiteSpace();
	}

	[Test]
	public async Task AuthProxy_CredentialNotLeakedToOriginAsync(CancellationToken cancellationToken)
	{
		string body = await F.AuthHttpClient.GetStringAsync($"http://localhost:{F.MockHttp.Port}/echo-headers", cancellationToken);
		await Assert.That(body).DoesNotContain("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);
	}

	[Test]
	public async Task SocketError_HostNotFound_Returns502Async(CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendViaFailingProxyAsync(SocketError.HostNotFound, cancellationToken);
		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
		await Assert.That(response.Headers.GetValues("X-Proxy-Error-Type").First()).IsEqualTo("HostUnreachable");
	}

	[Test]
	public async Task SocketError_ConnectionRefused_Returns502Async(CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendViaFailingProxyAsync(SocketError.ConnectionRefused, cancellationToken);
		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
		await Assert.That(response.Headers.GetValues("X-Proxy-Error-Type").First()).IsEqualTo("ConnectionRefused");
	}

	[Test]
	public async Task SocketError_ConnectionReset_Returns502Async(CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendViaFailingProxyAsync(SocketError.ConnectionReset, cancellationToken);
		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
		await Assert.That(response.Headers.GetValues("X-Proxy-Error-Type").First()).IsEqualTo("ConnectionReset");
	}

	[Test]
	public async Task SocketError_Other_Returns500Async(CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendViaFailingProxyAsync(SocketError.TimedOut, cancellationToken);
		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
		await Assert.That(response.Headers.GetValues("X-Proxy-Error-Type").First()).IsEqualTo("UnknownError");
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
		await Assert.That(sequence0.IsHttpHeader()).IsTrue();

		ReadOnlySequence<byte> sequence1 = TestUtils.GetMultiSegmentSequence(
			"GET / HTTP/1.1"u8.ToArray(),
			"\r\nHost: ip.sb\r\n"u8.ToArray(),
			"User-Agent: curl/7.55.1\r\n"u8.ToArray()
		);
		await Assert.That(sequence1.IsHttpHeader()).IsFalse();

		ReadOnlySequence<byte> sequence2 = TestUtils.GetMultiSegmentSequence(
			"\r\n"u8.ToArray(),
			"\r\n"u8.ToArray()
		);
		await Assert.That(sequence2.IsHttpHeader()).IsFalse();

		ReadOnlySequence<byte> sequence3 = TestUtils.GetMultiSegmentSequence(
			"GET HTTP/1.1"u8.ToArray(),
			"\r\nHost: ip.sb\r\n"u8.ToArray(),
			"User-Agent: curl/7.55.1\r\n"u8.ToArray(),
			"\r\n"u8.ToArray()
		);
		await Assert.That(sequence3.IsHttpHeader()).IsFalse();

		// Two spaces but version doesn't start with "HTTP/"
		ReadOnlySequence<byte> sequence4 = TestUtils.GetMultiSegmentSequence(
			"FOO BAR BAZ"u8.ToArray(),
			"\r\nHost: ip.sb\r\n"u8.ToArray(),
			"\r\n"u8.ToArray()
		);
		await Assert.That(sequence4.IsHttpHeader()).IsFalse();
	}

	[Test]
	public async Task AbsoluteUri_UsesUriAuthority_NotHostHeader(CancellationToken cancellationToken)
	{
		// GET http://real-host/path with Host: other-host.
		// Proxy MUST connect to real-host (from URI), not other-host (from Host header).
		// URI points to the real mock server; Host header points to unreachable port 1.
		string response = await SendRawRequestAsync(
			$"GET http://localhost:{F.MockHttp.Port}/get HTTP/1.1\r\nHost: localhost:1\r\n\r\n",
			cancellationToken);

		await Assert.That(response).Contains("HTTP/1.1 200");
	}

	[Test]
	public async Task InvalidContentLength_Returns502(CancellationToken cancellationToken)
	{
		HttpResponseMessage response = await F.HttpClient.GetAsync($"http://localhost:{F.MockHttp.Port}/bad-content-length", cancellationToken);

		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
	}

	[Test]
	public async Task ConflictingContentLength_Returns502(CancellationToken cancellationToken)
	{
		HttpResponseMessage response = await F.HttpClient.GetAsync($"http://localhost:{F.MockHttp.Port}/conflicting-content-length", cancellationToken);

		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
	}

	[Test]
	public async Task IncompleteRequestBody_DoesNotForwardNormally(CancellationToken cancellationToken)
	{
		// Client declares Content-Length: 100 but sends only 5 bytes then closes.
		// Proxy should detect the short read and NOT forward a 200 with incomplete body.
		using TcpClient tcp = new();
		NetworkStream ns = await ConnectToProxyAsync(tcp, cancellationToken);
		await ns.WriteAsync(Encoding.UTF8.GetBytes(
				$"POST http://localhost:{F.MockHttp.Port}/echo HTTP/1.1\r\nHost: localhost:{F.MockHttp.Port}\r\nContent-Length: 100\r\n\r\nhello"),
			cancellationToken);
		await ns.FlushAsync(cancellationToken);
		tcp.Client.Shutdown(SocketShutdown.Send);

		byte[] buf = new byte[4096];
		int n = await ns.ReadAsync(buf, cancellationToken);

		string response = Encoding.UTF8.GetString(buf, 0, n);

		// Must NOT get a 200 with the echoed (incomplete) body
		await Assert.That(response).DoesNotContain("HTTP/1.1 200");
	}

	[Test]
	public async Task QueryStringPreserved_WhenNoPathInAbsoluteUri(CancellationToken cancellationToken)
	{
		// Bug: "GET http://host?x=1" (no path) → proxy rewrites to "GET /" (query lost).
		// Fix: should rewrite to "GET /?x=1".
		// HttpClient normalizes URIs, so raw TCP is required for this edge case.
		// Mock server echoes request line for paths starting with "/?" (200 vs default 204).
		string response = await SendRawRequestAsync(
			$"GET http://localhost:{F.MockHttp.Port}?x=1 HTTP/1.1\r\nHost: localhost:{F.MockHttp.Port}\r\n\r\n",
			cancellationToken);

		await Assert.That(response).Contains("?x=1");
	}

	[Test]
	public async Task Connect_UsesRequestTarget_NotHostHeader(CancellationToken cancellationToken)
	{
		// CONNECT request-target is the real HTTPS server; Host header points to unreachable port.
		// Per RFC 9110 §9.3.6, the proxy MUST use request-target, not Host.
		using TcpClient tcp = new();
		NetworkStream ns = await ConnectToProxyAsync(tcp, cancellationToken);
		await ns.WriteAsync(Encoding.UTF8.GetBytes(
				$"CONNECT localhost:{F.MockHttps.Port} HTTP/1.1\r\nHost: localhost:1\r\n\r\n"),
			cancellationToken);
		await ns.FlushAsync(cancellationToken);

		// Read only the CONNECT response status line (tunnel stays open after 200).
		byte[] buf = new byte[1024];
		int n = await ns.ReadAsync(buf, cancellationToken);
		string response = Encoding.UTF8.GetString(buf, 0, n);

		await Assert.That(response).StartsWith("HTTP/1.1 200");
	}

	[Test]
	public async Task RelativeFormRequest_UsesHostHeader(CancellationToken cancellationToken)
	{
		// GET /echo-request-line (relative-form) with Host pointing to the mock server.
		// Proxy MUST use Host header as target (not "/echo-request-line" as authority).
		string response = await SendRawRequestAsync(
			$"GET /echo-request-line HTTP/1.1\r\nHost: localhost:{F.MockHttp.Port}\r\n\r\n",
			cancellationToken);

		await Assert.That(response).Contains("HTTP/1.1 200");
	}

	[Test]
	public async Task RelativeFormRequest_QueryContainsScheme_UsesHostHeader(CancellationToken cancellationToken)
	{
		// GET /echo-request-line?next=http://a/b (origin-form with "://" in query).
		// Proxy MUST treat this as relative-form: use Host header, preserve path+query as-is.
		string response = await SendRawRequestAsync(
			$"GET /echo-request-line?next=http://a/b HTTP/1.1\r\nHost: localhost:{F.MockHttp.Port}\r\n\r\n",
			cancellationToken);

		await Assert.That(response).Contains("HTTP/1.1 200");
		// Verify the request-line forwarded to the server preserves the original path+query
		await Assert.That(response).Contains("/echo-request-line?next=http://a/b");
	}

	[Test]
	public async Task TruncatedUpstreamHeaders_Returns502(CancellationToken cancellationToken)
	{
		// Upstream closes before sending complete headers (\r\n\r\n).
		// Proxy should return 502, not 500.
		HttpResponseMessage response = await F.HttpClient.GetAsync(
			$"http://localhost:{F.MockHttp.Port}/truncated-headers",
			cancellationToken);

		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
	}

	[Test]
	public async Task GarbageUpstreamResponse_Returns502(CancellationToken cancellationToken)
	{
		// Upstream sends non-HTTP response (status line doesn't start with "HTTP/").
		// Proxy should return 502, not forward garbage.
		HttpResponseMessage response = await F.HttpClient.GetAsync($"http://localhost:{F.MockHttp.Port}/garbage-response", cancellationToken);

		await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
	}

	#region helpers

	private static async Task AcceptLoopAsync(TcpListener listener, IInbound inbound, IOutbound outbound, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				Socket socket = await listener.AcceptSocketAsync(cancellationToken);
				socket.NoDelay = true;
				_ = HandleAsync(socket, inbound, outbound, cancellationToken);
			}
		}
		catch (OperationCanceledException) { }

		return;

		static async Task HandleAsync(Socket socket, IInbound inbound, IOutbound outbound, CancellationToken cancellationToken)
		{
			try
			{
				IDuplexPipe pipe = socket.AsDuplexPipe();
				await inbound.HandleAsync(pipe, outbound, cancellationToken);
			}
			finally
			{
				socket.FullClose();
			}
		}
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

				if (n is 0)
				{
					break;
				}

				totalRead += n;
			}
		}
		catch (OperationCanceledException) { }

		return Encoding.UTF8.GetString(buf, 0, totalRead);
	}

	private static async Task<NetworkStream> ConnectToProxyAsync(TcpClient tcp, CancellationToken cancellationToken)
	{
		IPEndPoint proxyEndpoint = (IPEndPoint)F.ProxyListener.LocalEndpoint;
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

	private sealed class FailingOutbound(SocketError error) : IOutbound
	{
		public ValueTask<IConnection> ConnectAsync(ProxyDestination destination, CancellationToken cancellationToken)
		{
			throw new SocketException((int)error);
		}
	}

	private static async Task<HttpResponseMessage> SendViaFailingProxyAsync(SocketError error, CancellationToken cancellationToken)
	{
		HttpForwarder forwarder = new();
		FailingOutbound outbound = new(error);
		TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_ = AcceptLoopAsync(listener, forwarder, outbound, cts.Token);

		try
		{
			ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
			using HttpClient client = new(new SocketsHttpHandler
			{
				UseProxy = true,
				Proxy = new WebProxy(IPAddress.Loopback.ToString(), port)
			});
			return await client.GetAsync("http://localhost:1/test", cancellationToken);
		}
		finally
		{
			await cts.CancelAsync();
			listener.Stop();
		}
	}

	#endregion
}
