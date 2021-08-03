using HttpProxy.Enums;
using Pipelines.Extensions;
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HttpProxy
{
	public class HttpProxyHandShake
	{
		private readonly IDuplexPipe _pipe;

		private static readonly string[] NewLines = { "\r\n", "\r", "\n" };

		public bool IsConnect { get; private set; }
		public bool IsKeepAlive { get; private set; }

		public string? HttpVersion { get; private set; }
		public string? HostHeader { get; private set; }
		public string? Request { get; private set; }
		public string? Hostname { get; private set; }
		public ushort Port { get; private set; }

		public string ProxyAgent { get; set; } = @"HMBSbige.HttpProxy 1.0";

		private static ReadOnlySpan<byte> HttpHeaderEnd => new[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };

		public HttpProxyHandShake(IDuplexPipe pipe)
		{
			_pipe = pipe;
		}

		public static bool IsHttpHeader(ReadOnlySequence<byte> buffer)
		{
			var reader = new SequenceReader<byte>(buffer);
			if (!reader.TryReadTo(out ReadOnlySequence<byte> headerBuffer, HttpHeaderEnd))
			{
				return false;
			}

			var headers = Encoding.UTF8.GetString(headerBuffer); // 不包括结尾的 \r\n\r\n

			var headerLines = headers.Split(NewLines, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

			if (headerLines.Length <= 0)
			{
				return false;
			}

			var methodLine = headerLines[0].Split(' ');
			if (methodLine.Length is not 3) // METHOD URI HTTP/X.Y
			{
				return false; // InvalidRequest
			}

			return true;
		}

		public async ValueTask<bool> TryParseHeaderAsync(CancellationToken token = default)
		{
			var result = await _pipe.Input.ReadAsync(token);
			var buffer = result.Buffer;
			try
			{
				return TryParseHeader(ref buffer);
			}
			finally
			{
				_pipe.Input.AdvanceTo(buffer.Start, buffer.End);
			}
		}

		private bool TryParseHeader(ref ReadOnlySequence<byte> buffer)
		{
			var reader = new SequenceReader<byte>(buffer);
			if (!reader.TryReadTo(out ReadOnlySequence<byte> headerBuffer, HttpHeaderEnd))
			{
				return false;
			}

			var headers = Encoding.UTF8.GetString(headerBuffer); // 不包括结尾的 \r\n\r\n

			var headerLines = headers.Split(NewLines, StringSplitOptions.RemoveEmptyEntries);

			if (headerLines.Length <= 0)
			{
				return false;
			}

			var methodLine = headerLines[0].Split(' ');
			if (methodLine.Length is not 3) // METHOD URI HTTP/X.Y
			{
				return false; // InvalidRequest
			}

			var method = methodLine[0];
			HttpVersion = methodLine[2];
			IsConnect = method.Equals(@"CONNECT", StringComparison.OrdinalIgnoreCase);
			IsKeepAlive = true;

			if (IsConnect)
			{
				foreach (var headerLine in headerLines)
				{
					var sp = headerLine.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
					if (sp.Length != 2)
					{
						return false; // InvalidRequest
					}

					var headerName = sp[0];
					if (headerName.Equals(@"Host", StringComparison.OrdinalIgnoreCase))
					{
						HostHeader = sp[1];
						break;
					}
				}
			}
			else
			{
				var hostUri = new Uri(methodLine[1]);
				var request = new StringBuilder(8192);

				request.Append(method);
				request.Append(' ');
				request.Append(hostUri.PathAndQuery);
				request.Append(hostUri.Fragment);
				request.Append(' ');
				request.Append(HttpVersion);

				for (var i = 1; i < headerLines.Length; ++i)
				{
					var sp = headerLines[i].Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
					if (sp.Length != 2)
					{
						continue;
					}

					var headerName = sp[0];
					var headerValue = sp[1];

					if (headerName.Equals(@"Proxy-Connection", StringComparison.OrdinalIgnoreCase))
					{
						headerName = @"Connection";
					}

					if (headerName.Equals(@"Connection", StringComparison.OrdinalIgnoreCase))
					{
						if (headerValue.Equals(@"Close", StringComparison.OrdinalIgnoreCase))
						{
							IsKeepAlive = false;
						}
					}
					else if (headerName.Equals(@"Host", StringComparison.OrdinalIgnoreCase))
					{
						HostHeader = headerValue;
					}

					if (!headerName.IsHopByHopHeader())
					{
						request.Append("\r\n");
						request.Append(headerLines[i]);
					}
				}

				if (string.IsNullOrEmpty(HostHeader))
				{
					// No host header???
					request.Append("\r\nHost: ");
					request.Append(hostUri.Host);
				}

				request.Append("\r\n\r\n");
				Request = request.ToString();
			}

			Port = 80;
			if (string.IsNullOrEmpty(HostHeader))
			{
				var requestTarget = methodLine[1];
				Hostname = requestTarget;

				var colon = requestTarget.LastIndexOf(':');
				if (colon is not -1 && ushort.TryParse(requestTarget.AsSpan(colon + 1), out var port))
				{
					Port = port;
					Hostname = requestTarget[..colon];
				}
			}
			else
			{
				var colon = HostHeader.LastIndexOf(':');
				if (colon is -1)
				{
					Hostname = HostHeader;
					var requestTarget = methodLine[1];
					colon = requestTarget.LastIndexOf(':');
					if (colon is not -1 && ushort.TryParse(requestTarget.AsSpan(colon + 1), out var port))
					{
						Port = port;
					}
				}
				else
				{
					Hostname = HostHeader[..colon];
					if (ushort.TryParse(HostHeader.AsSpan(colon + 1), out var port))
					{
						Port = port;
					}
				}
			}


			buffer = buffer.Slice(reader.Consumed);
			return true;
		}

		private async ValueTask SendStringAsync(string str, CancellationToken token = default)
		{
			await _pipe.Output.WriteAsync(str, token);
		}

		public ValueTask SendErrorAsync(string httpVersion = @"HTTP/1.1", CancellationToken token = default)
		{
			return SendStringAsync(Utils.BuildErrorResponse(ConnectionErrorResult.InvalidRequest, httpVersion), token);
		}

		public ValueTask SendConnectSuccessAsync(CancellationToken token = default)
		{
			return SendStringAsync($"{HttpVersion} 200 Connection Established\r\nConnection: close\r\nProxy-Agent: {ProxyAgent}\r\n\r\n", token);
		}
	}
}
