using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace UnitTest.TestBase;

public sealed class MockHttpServer : IDisposable
{
	private readonly TcpListener _listener = new(IPAddress.IPv6Any, 0) { Server = { DualMode = true } };
	private readonly CancellationTokenSource _cts = new();
	private X509Certificate2? _cert;

	public bool UseTls { get; init; }

	public int Port { get; private set; }

	public void Start()
	{
		if (UseTls)
		{
			_cert = CreateSelfSignedCert();
		}

		_listener.Start();
		Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
		_ = RunAsync();
	}

	private async Task RunAsync()
	{
		try
		{
			while (!_cts.IsCancellationRequested)
			{
				Socket socket = await _listener.AcceptSocketAsync(_cts.Token);
				_ = HandleAsync(socket);
			}
		}
		catch (OperationCanceledException) { }
		catch (ObjectDisposedException) { }
	}

	private async Task HandleAsync(Socket socket)
	{
		try
		{
			Stream stream = new NetworkStream(socket, ownsSocket: true);

			if (_cert is not null)
			{
				SslStream ssl = new(stream, leaveInnerStreamOpen: false);
				await ssl.AuthenticateAsServerAsync(_cert);
				stream = ssl;
			}

			await using Stream _ = stream;

			byte[] buf = new byte[4096];
			int total = 0;

			while (total < buf.Length)
			{
				int n = await stream.ReadAsync(buf.AsMemory(total));

				if (n is 0)
				{
					break;
				}

				total += n;

				if (buf.AsSpan(0, total).IndexOf("\r\n\r\n"u8) >= 0)
				{
					break;
				}
			}

			string firstLine = Encoding.UTF8.GetString(buf, 0, total).Split("\r\n")[0];
			string[] parts = firstLine.Split(' ');
			string path = parts.Length > 1 ? parts[1] : "/";

			if (path.StartsWith("/stream-bytes/") && int.TryParse(path["/stream-bytes/".Length..], out int count))
			{
				await WriteChunkedAsync(stream, count, delay: true);
			}
			else if (path is "/status/204")
			{
				await WriteAsync(stream, 204, "No Content");
			}
			else if (path is "/get")
			{
				await WriteAsync(stream, 200, "OK", """{"message":"hello"}""");
			}
			else if (path is "/set-cookies")
			{
				await stream.WriteAsync("HTTP/1.1 200 OK\r\nSet-Cookie: a=1\r\nSet-Cookie: b=2\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
				await stream.FlushAsync();
			}
			else if (path is "/echo")
			{
				await WriteEchoAsync(stream, buf, total);
			}
			else if (path is "/echo-te")
			{
				await WriteEchoTeAsync(stream, buf, total);
			}
			else if (path is "/echo-request-line" || path.StartsWith("/echo-request-line?"))
			{
				await WriteAsync(stream, 200, "OK", firstLine);
			}
			else if (path is "/echo-headers")
			{
				string allHeaders = Encoding.UTF8.GetString(buf, 0, total);
				int headerEnd = allHeaders.IndexOf("\r\n\r\n", StringComparison.Ordinal);
				string headersOnly = headerEnd >= 0 ? allHeaders[..headerEnd] : allHeaders;
				await WriteAsync(stream, 200, "OK", headersOnly);
			}
			else if (path is "/te-gzip-response")
			{
				// Response with Transfer-Encoding: gzip (close-delimited, no chunked)
				await stream.WriteAsync("HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip\r\nConnection: close\r\n\r\n"u8.ToArray());
				await stream.WriteAsync(new byte[] { 0x1f, 0x8b, 0x08, 0x00 });
				await stream.FlushAsync();
			}
			else if (path is "/close-delimited")
			{
				await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n"u8.ToArray());
				await stream.WriteAsync("""{"message":"close-delimited"}"""u8.ToArray());
				await stream.FlushAsync();
			}
			else if (path is "/bad-content-length")
			{
				await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: abc\r\nConnection: close\r\n\r\nhello"u8.ToArray());
				await stream.FlushAsync();
			}
			else if (path is "/conflicting-content-length")
			{
				await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nContent-Length: 10\r\nConnection: close\r\n\r\nhello"u8.ToArray());
				await stream.FlushAsync();
			}
			else if (path.StartsWith("/stream-bytes-ext/") && int.TryParse(path["/stream-bytes-ext/".Length..], out int extCount))
			{
				await WriteChunkedAsync(stream, extCount, chunkExtension: ";ext=val", terminator: "0;ext=final\r\n\r\n");
			}
			else if (path.StartsWith("/stream-bytes-trailer/") && int.TryParse(path["/stream-bytes-trailer/".Length..], out int trailerCount))
			{
				await WriteChunkedAsync(stream, trailerCount, terminator: "0\r\nX-Checksum: abc123\r\n\r\n");
			}
			else if (path is "/truncated-headers")
			{
				// Write partial headers then close — no \r\n\r\n
				await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-"u8.ToArray());
				await stream.FlushAsync();
				return;// close without shutdown — skip graceful teardown below
			}
			else if (path is "/garbage-response")
			{
				// Write non-HTTP data as response
				await stream.WriteAsync("XYZZY totally not HTTP\r\n\r\n"u8.ToArray());
				await stream.FlushAsync();
			}
			else if (path is "/framing/single-write")
			{
				await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello"u8.ToArray());
				await stream.FlushAsync();
			}
			else if (path is "/framing/separate-writes")
			{
				await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\n"u8.ToArray());
				await stream.FlushAsync();
				await stream.WriteAsync("hello"u8.ToArray());
				await stream.FlushAsync();
			}
			else if (path is "/framing/split-mid-header")
			{
				await stream.WriteAsync("HTTP/1.1 200 OK\r\nCont"u8.ToArray());
				await stream.FlushAsync();
				await Task.Delay(50);
				await stream.WriteAsync("ent-Length: 5\r\nConnection: close\r\n\r\nhello"u8.ToArray());
				await stream.FlushAsync();
			}
			else if (path is "/framing/split-mid-body")
			{
				await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhel"u8.ToArray());
				await stream.FlushAsync();
				await Task.Delay(50);
				await stream.WriteAsync("lo"u8.ToArray());
				await stream.FlushAsync();
			}
			else if (path.StartsWith("/?"))
			{
				// Echo full request line — used by path-less query-string tests
				await WriteAsync(stream, 200, "OK", firstLine);
			}
			else
			{
				await WriteAsync(stream, 204, "No Content");
			}

			// Graceful shutdown: send FIN on write side, then drain remaining
			// request body. Without this, closing the socket with unread data
			// can cause TCP RST, which discards buffered response data at the
			// SOCKS5 relay before the proxy reads it.
			socket.Shutdown(SocketShutdown.Send);

			byte[] drain = new byte[4096];

			while (await stream.ReadAsync(drain) > 0)
			{
			}
		}
		catch (IOException ex)
		{
			Debug.WriteLine($"MockHttpServer: {ex.GetType().Name}: {ex.Message}");
		}
		catch (SocketException ex)
		{
			Debug.WriteLine($"MockHttpServer: {ex.GetType().Name}: {ex.SocketErrorCode}");
		}
	}

	private static async Task WriteAsync(Stream stream, int code, string reason, string? body = null)
	{
		StringBuilder sb = new();
		sb.Append($"HTTP/1.1 {code} {reason}\r\n");

		if (body is not null)
		{
			int byteCount = Encoding.UTF8.GetByteCount(body);
			sb.Append($"Content-Length: {byteCount}\r\n");
			sb.Append("Content-Type: application/json\r\n");
		}

		sb.Append("Connection: close\r\n\r\n");

		if (body is not null)
		{
			sb.Append(body);
		}

		await stream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()));
		await stream.FlushAsync();
	}

	private static async Task WriteChunkedAsync(
		Stream stream, int byteCount,
		string? chunkExtension = null,
		string? terminator = null,
		bool delay = false)
	{
		await stream.WriteAsync("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"u8.ToArray());

		byte[] data = new byte[byteCount];
		Random.Shared.NextBytes(data);

		int offset = 0;
		int chunkSize = Math.Max(1, byteCount / 4);

		while (offset < byteCount)
		{
			int len = Math.Min(chunkSize, byteCount - offset);
			await stream.WriteAsync(Encoding.UTF8.GetBytes($"{len:x}{chunkExtension}\r\n"));
			await stream.WriteAsync(data.AsMemory(offset, len));
			await stream.WriteAsync("\r\n"u8.ToArray());
			offset += len;

			if (delay)
			{
				await Task.Delay(100);
			}
		}

		await stream.WriteAsync(Encoding.UTF8.GetBytes(terminator ?? "0\r\n\r\n"));
		await stream.FlushAsync();
	}

	private static async Task WriteEchoAsync(Stream stream, byte[] requestBuf, int requestTotal)
	{
		ReadOnlySpan<byte> span = requestBuf.AsSpan(0, requestTotal);
		int headerEndIdx = span.IndexOf("\r\n\r\n"u8);
		int bodyStartIdx = headerEndIdx + 4;

		int contentLength = 0;
		bool isChunked = false;
		string headers = Encoding.UTF8.GetString(requestBuf, 0, headerEndIdx);

		foreach (string line in headers.Split("\r\n"))
		{
			if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
			{
				int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), out contentLength);
			}
			else if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
			{
				ReadOnlySpan<char> teValue = line.AsSpan("Transfer-Encoding:".Length).Trim();
				int lastComma = teValue.LastIndexOf(',');
				ReadOnlySpan<char> lastToken = lastComma < 0 ? teValue : teValue.Slice(lastComma + 1);
				isChunked = lastToken.Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase);
			}
		}

		byte[] body = [];

		if (isChunked)
		{
			body = await ReadChunkedBodyAsync(stream, requestBuf, requestTotal, bodyStartIdx);
		}
		else if (contentLength > 0)
		{
			body = new byte[contentLength];
			int alreadyRead = Math.Min(requestTotal - bodyStartIdx, contentLength);
			requestBuf.AsSpan(bodyStartIdx, alreadyRead).CopyTo(body);

			int remaining = contentLength - alreadyRead;
			int offset = alreadyRead;

			while (remaining > 0)
			{
				int n = await stream.ReadAsync(body.AsMemory(offset, remaining));

				if (n is 0)
				{
					break;
				}

				offset += n;
				remaining -= n;
			}
		}

		await WriteAsync(stream, 200, "OK", Encoding.UTF8.GetString(body));
	}

	private static async Task<byte[]> ReadChunkedBodyAsync(Stream stream, byte[] requestBuf, int requestTotal, int bodyStartIdx)
	{
		byte[] buf = new byte[8192];
		int len = requestTotal - bodyStartIdx;
		requestBuf.AsSpan(bodyStartIdx, len).CopyTo(buf);

		// All valid chunked bodies end with \r\n\r\n (0\r\n\r\n or 0\r\ntrailer\r\n\r\n)
		while (!buf.AsSpan(0, len).EndsWith("\r\n\r\n"u8))
		{
			int n = await stream.ReadAsync(buf.AsMemory(len));

			if (n is 0)
			{
				break;
			}

			len += n;
		}

		// Decode chunks in-place — write pointer never overtakes read pointer
		int decodedLen = 0;
		int pos = 0;

		while (pos < len)
		{
			int lineEnd = buf.AsSpan(pos, len - pos).IndexOf("\r\n"u8);

			if (lineEnd < 0)
			{
				break;
			}

			string sizeLine = Encoding.ASCII.GetString(buf, pos, lineEnd);
			int semi = sizeLine.IndexOf(';');

			if (semi >= 0)
			{
				sizeLine = sizeLine[..semi];
			}

			int chunkSize = Convert.ToInt32(sizeLine.Trim(), 16);
			pos += lineEnd + 2;

			if (chunkSize is 0)
			{
				break;
			}

			buf.AsSpan(pos, chunkSize).CopyTo(buf.AsSpan(decodedLen));
			decodedLen += chunkSize;
			pos += chunkSize + 2; // skip data + \r\n
		}

		return buf[..decodedLen];
	}

	private static async Task WriteEchoTeAsync(Stream stream, byte[] requestBuf, int requestTotal)
	{
		string headers = Encoding.UTF8.GetString(requestBuf, 0, requestTotal);
		List<string> teValues = [];

		foreach (string line in headers.Split("\r\n"))
		{
			if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
			{
				teValues.Add(line.Substring("Transfer-Encoding:".Length).Trim());
			}
		}

		await WriteAsync(stream, 200, "OK", string.Join(", ", teValues));
	}

	private static X509Certificate2 CreateSelfSignedCert()
	{
		using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
		CertificateRequest req = new("CN=localhost", ecdsa, HashAlgorithmName.SHA256);
		SubjectAlternativeNameBuilder san = new();
		san.AddIpAddress(IPAddress.Loopback);
		san.AddDnsName("localhost");
		req.CertificateExtensions.Add(san.Build());
		X509Certificate2 cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
		return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
	}

	public void Dispose()
	{
		_cts.Cancel();
		_listener.Stop();
		_cert?.Dispose();
		_cts.Dispose();
	}
}
