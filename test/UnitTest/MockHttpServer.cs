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
					await WriteChunkedAsync(stream, count);
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
				else
				{
					await WriteAsync(stream, 204, "No Content");
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

	private static async Task WriteChunkedAsync(Stream stream, int byteCount)
	{
		await stream.WriteAsync("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"u8.ToArray());

		byte[] data = new byte[byteCount];
		Random.Shared.NextBytes(data);

		int offset = 0;
		int chunkSize = Math.Max(1, byteCount / 4);

		while (offset < byteCount)
		{
			int len = Math.Min(chunkSize, byteCount - offset);
			await stream.WriteAsync(Encoding.UTF8.GetBytes($"{len:x}\r\n"));
			await stream.WriteAsync(data.AsMemory(offset, len));
			await stream.WriteAsync("\r\n"u8.ToArray());
			offset += len;
			await Task.Delay(100);
		}

		await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
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
