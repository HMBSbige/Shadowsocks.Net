using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace UnitTest;

internal sealed class MockHttpServer : IDisposable
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

			await using (stream)
			{
				byte[] buf = new byte[4096];
				int total = 0;

				while (total < buf.Length)
				{
					int n = await stream.ReadAsync(buf.AsMemory(total));

					if (n == 0)
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

				if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri))
				{
					path = uri.AbsolutePath;
				}

				if (path.StartsWith("/stream-bytes/") && int.TryParse(path["/stream-bytes/".Length..], out int count))
				{
					await WriteChunkedAsync(stream, count, delay: true);
				}
				else if (path == "/status/204")
				{
					await WriteAsync(stream, 204, "No Content");
				}
				else if (path == "/get")
				{
					await WriteAsync(stream, 200, "OK", """{"message":"hello"}""");
				}
				else if (path == "/set-cookies")
				{
					await stream.WriteAsync("HTTP/1.1 200 OK\r\nSet-Cookie: a=1\r\nSet-Cookie: b=2\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
					await stream.FlushAsync();
				}
				else if (path == "/echo")
				{
					await WriteEchoAsync(stream, buf, total);
				}
				else if (path == "/echo-te")
				{
					await WriteEchoTeAsync(stream, buf, total);
				}
				else if (path == "/echo-headers")
				{
					string allHeaders = Encoding.UTF8.GetString(buf, 0, total);
					int headerEnd = allHeaders.IndexOf("\r\n\r\n", StringComparison.Ordinal);
					string headersOnly = headerEnd >= 0 ? allHeaders[..headerEnd] : allHeaders;
					await WriteAsync(stream, 200, "OK", headersOnly);
				}
				else if (path == "/te-gzip-response")
				{
					// Response with Transfer-Encoding: gzip (close-delimited, no chunked)
					await stream.WriteAsync("HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip\r\nConnection: close\r\n\r\n"u8.ToArray());
					await stream.WriteAsync(new byte[] { 0x1f, 0x8b, 0x08, 0x00 });
					await stream.FlushAsync();
				}
				else if (path == "/close-delimited")
				{
					await WriteCloseDelimitedAsync(stream);
				}
				else if (path.StartsWith("/stream-bytes-ext/") && int.TryParse(path["/stream-bytes-ext/".Length..], out int extCount))
				{
					await WriteChunkedAsync(stream, extCount, chunkExtension: ";ext=val", terminator: "0;ext=final\r\n\r\n");
				}
				else if (path.StartsWith("/stream-bytes-trailer/") && int.TryParse(path["/stream-bytes-trailer/".Length..], out int trailerCount))
				{
					await WriteChunkedAsync(stream, trailerCount, terminator: "0\r\nX-Checksum: abc123\r\n\r\n");
				}
				else
				{
					await WriteAsync(stream, 204, "No Content");
				}

				// Graceful shutdown: send FIN on write side, then drain remaining
				// request body. Without this, closing the socket with unread data
				// can cause TCP RST, which discards buffered response data at the
				// SOCKS5 relay before the proxy reads it.
				try
				{
					socket.Shutdown(SocketShutdown.Send);
				}
				catch
				{
					// ignored
				}

				try
				{
					byte[] drain = new byte[4096];

					while (await stream.ReadAsync(drain) > 0)
					{
					}
				}
				catch
				{
					// ignored
				}
			}
		}
		catch
		{
			// ignored
		}
	}

	private static async Task WriteAsync(Stream stream, int code, string reason, string? body = null)
	{
		StringBuilder sb = new();
		sb.Append($"HTTP/1.1 {code} {reason}\r\n");

		if (body is not null)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(body);
			sb.Append($"Content-Length: {bytes.Length}\r\n");
			sb.Append("Content-Type: application/json\r\n");
		}

		sb.Append("Connection: close\r\n\r\n");
		await stream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()));

		if (body is not null)
		{
			await stream.WriteAsync(Encoding.UTF8.GetBytes(body));
		}

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

		if (headerEndIdx < 0)
		{
			await WriteAsync(stream, 400, "Bad Request");
			return;
		}

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

		if (contentLength > 0)
		{
			body = new byte[contentLength];
			int alreadyRead = Math.Min(requestTotal - bodyStartIdx, contentLength);

			if (alreadyRead > 0)
			{
				Buffer.BlockCopy(requestBuf, bodyStartIdx, body, 0, alreadyRead);
			}

			int remaining = contentLength - alreadyRead;
			int offset = alreadyRead;

			while (remaining > 0)
			{
				int n = await stream.ReadAsync(body.AsMemory(offset, remaining));
				if (n == 0)
					break;
				offset += n;
				remaining -= n;
			}
		}
		else if (isChunked)
		{
			body = await ReadChunkedBodyAsync(stream, requestBuf, requestTotal, bodyStartIdx);
		}

		await WriteAsync(stream, 200, "OK", Encoding.UTF8.GetString(body));
	}

	private static async Task<byte[]> ReadChunkedBodyAsync(Stream stream, byte[] requestBuf, int requestTotal, int bodyStartIdx)
	{
		// Gather raw chunked data: bytes already in buffer + read more from stream
		MemoryStream raw = new();
		raw.Write(requestBuf, bodyStartIdx, requestTotal - bodyStartIdx);

		byte[] readBuf = new byte[4096];

		// Read until we have the terminating chunk (use GetBuffer to avoid O(n²) copies)
		while (!HasTerminatingChunk(raw.GetBuffer().AsSpan(0, (int)raw.Length)))
		{
			int n = await stream.ReadAsync(readBuf);
			if (n == 0)
				break;
			raw.Write(readBuf, 0, n);
		}

		// Decode chunks (reuse the underlying buffer)
		byte[] data = raw.GetBuffer();
		int dataLen = (int)raw.Length;
		MemoryStream decoded = new();
		int pos = 0;

		while (pos < dataLen)
		{
			int lineEnd = data.AsSpan(pos, dataLen - pos).IndexOf("\r\n"u8);
			if (lineEnd < 0)
				break;

			string sizeLine = Encoding.ASCII.GetString(data, pos, lineEnd);
			int semi = sizeLine.IndexOf(';');
			if (semi >= 0)
				sizeLine = sizeLine[..semi];

			int chunkSize = Convert.ToInt32(sizeLine.Trim(), 16);
			pos += lineEnd + 2;

			if (chunkSize == 0)
				break;

			decoded.Write(data, pos, chunkSize);
			pos += chunkSize + 2;// skip data + \r\n
		}

		return decoded.ToArray();

		static bool HasTerminatingChunk(ReadOnlySpan<byte> span)
		{
			// Look for "0\r\n" as a chunk-size line followed by "\r\n" (end of trailers)
			int idx = 0;

			while (idx < span.Length)
			{
				int lineEnd = span[idx..].IndexOf("\r\n"u8);
				if (lineEnd < 0)
					return false;

				string sizePart = Encoding.ASCII.GetString(span.Slice(idx, lineEnd));
				int semi = sizePart.IndexOf(';');
				if (semi >= 0)
					sizePart = sizePart[..semi];

				if (int.TryParse(sizePart.Trim(), NumberStyles.HexNumber, null, out int size) && size == 0)
				{
					// Found terminating chunk line, check there's at least \r\n after it
					return idx + lineEnd + 2 + 2 <= span.Length;
				}

				// Skip chunk: size-line + data + \r\n
				idx += lineEnd + 2 + size + 2;
			}

			return false;
		}
	}

	private static async Task WriteEchoTeAsync(Stream stream, byte[] requestBuf, int requestTotal)
	{
		string headers = Encoding.UTF8.GetString(requestBuf, 0, requestTotal);
		string teValue = "";

		foreach (string line in headers.Split("\r\n"))
		{
			if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
			{
				teValue = line.Substring("Transfer-Encoding:".Length).Trim();
				break;
			}
		}

		await WriteAsync(stream, 200, "OK", teValue);
	}

	private static async Task WriteCloseDelimitedAsync(Stream stream)
	{
		byte[] body = """{"message":"close-delimited"}"""u8.ToArray();
		await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n"u8.ToArray());
		await stream.WriteAsync(body);
		await stream.FlushAsync();
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
