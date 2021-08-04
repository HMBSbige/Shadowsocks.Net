using HttpProxy.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pipelines.Extensions;
using Socks5.Clients;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static HttpProxy.HttpUtils;

namespace HttpProxy
{
	public class HttpToSocks5
	{
		private const string LogHeader = @"[HttpToSocks5]";

		private readonly ILogger<HttpToSocks5> _logger;

		public HttpToSocks5(ILogger<HttpToSocks5>? logger = null)
		{
			_logger = logger ?? NullLogger<HttpToSocks5>.Instance;
		}

		public async ValueTask ForwardToSocks5Async(IDuplexPipe incomingPipe, IPEndPoint socks5, CancellationToken cancellationToken = default)
		{
			var headers = await ReadHttpHeadersAsync(incomingPipe.Input, cancellationToken);
			_logger.LogDebug("{0} Client headers received: \n{1}", LogHeader, headers);

			if (!TryParseHeader(headers, out var httpHeaders))
			{
				await SendErrorAsync(incomingPipe.Output, ConnectionErrorResult.InvalidRequest, token: cancellationToken);
				return;
			}
			_logger.LogDebug("{0} New request headers: \n{1}", LogHeader, httpHeaders);

			if (httpHeaders.IsConnect)
			{
				if (httpHeaders.Hostname is null)
				{
					await SendErrorAsync(incomingPipe.Output, ConnectionErrorResult.InvalidRequest, httpHeaders.HttpVersion, cancellationToken);
					return;
				}

				await using var socks5Client = new Socks5Client(socks5);
				await socks5Client.ConnectAsync(httpHeaders.Hostname, httpHeaders.Port, cancellationToken);

				await SendConnectSuccessAsync(incomingPipe.Output, httpHeaders.HttpVersion, cancellationToken);

				var socks5Pipe = socks5Client.GetPipe();

				await socks5Pipe.LinkToAsync(incomingPipe, cancellationToken);
			}
			else
			{
				if (httpHeaders.Hostname is null || httpHeaders.Request is null)
				{
					await SendErrorAsync(incomingPipe.Output, ConnectionErrorResult.InvalidRequest, httpHeaders.HttpVersion, cancellationToken);
					return;
				}
				await using var socks5Client = new Socks5Client(socks5);
				await socks5Client.ConnectAsync(httpHeaders.Hostname, httpHeaders.Port, cancellationToken);
				var socks5Pipe = socks5Client.GetPipe();

				await socks5Pipe.Output.WriteAsync(httpHeaders.Request, cancellationToken);
				if (httpHeaders.ContentLength > 0)
				{
					_logger.LogDebug(@"{0} Waiting for up to {1} bytes from client", LogHeader, httpHeaders.ContentLength);

					await incomingPipe.Input.CopyToAsync(socks5Pipe.Output, httpHeaders.ContentLength, cancellationToken);

					_logger.LogDebug(@"{0} client sent {1} bytes to server", LogHeader, httpHeaders.ContentLength);
				}

				//Read response
				var responseHeaders = await ReadHttpHeadersAsync(socks5Pipe.Input, cancellationToken);
				_logger.LogDebug("{0} server headers received: \n{1}", LogHeader, responseHeaders);
				if (!TryReadContentLength(responseHeaders, out var serverResponseContentLength))
				{
					await SendErrorAsync(incomingPipe.Output, ConnectionErrorResult.UnknownError, httpHeaders.HttpVersion, cancellationToken);
					return;
				}

				incomingPipe.Output.Write(responseHeaders);
				incomingPipe.Output.Write(HttpHeaderEnd);
				await incomingPipe.Output.FlushAsync(cancellationToken);
				if (serverResponseContentLength > 0)
				{
					_logger.LogDebug(@"{0} Waiting for up to {1} bytes from server", LogHeader, serverResponseContentLength);

					await socks5Pipe.Input.CopyToAsync(incomingPipe.Output, serverResponseContentLength, cancellationToken);

					_logger.LogDebug(@"{0} server sent {1} bytes to client", LogHeader, serverResponseContentLength);
				}
			}
		}

		private static async ValueTask SendErrorAsync(PipeWriter writer, ConnectionErrorResult error, string httpVersion = @"HTTP/1.1", CancellationToken token = default)
		{
			var str = HttpUtils.BuildErrorResponse(error, httpVersion);
			await writer.WriteAsync(str, token);
		}

		private static async ValueTask SendConnectSuccessAsync(PipeWriter writer, string httpVersion = @"HTTP/1.1", CancellationToken token = default)
		{
			var str = $"{httpVersion} 200 Connection Established\r\n\r\n";
			await writer.WriteAsync(str, token);
		}

		private static bool TryParseHeader(string raw, [NotNullWhen(true)] out HttpHeaders? httpHeaders)
		{
			var headerLines = raw.Split(NewLines, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

			if (headerLines.Length <= 0)
			{
				goto InvalidRequest;
			}

			var methodLine = headerLines[0].Split(' ');
			if (methodLine.Length is not 3) // METHOD URI HTTP/X.Y
			{
				goto InvalidRequest;
			}

			#region Read Headers

			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var headerLine in headerLines.Skip(1))
			{
				var sp = headerLine.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (sp.Length != 2)
				{
					goto InvalidRequest;
				}

				var headerName = sp[0];
				var headerValue = sp[1];

				if (headerName.IsHopByHopHeader())
				{
					continue;
				}

				headers[headerName] = headerValue;
			}

			headers[@"Connection"] = @"close";

			#endregion

			httpHeaders = new HttpHeaders
			{
				Method = methodLine[0],
				HostUriString = methodLine[1],
				HttpVersion = methodLine[2]
			};

			#region Host

			headers.TryGetValue(@"Host", out var hostHeader);
			if (string.IsNullOrEmpty(hostHeader) && httpHeaders.IsConnect)
			{
				hostHeader = httpHeaders.HostUriString;
			}

			var hostUri = new Uri(httpHeaders.HostUriString);
			if (string.IsNullOrEmpty(hostHeader))
			{
				httpHeaders.Hostname = hostUri.Host;
				httpHeaders.Port = (ushort)hostUri.Port;
			}
			else
			{
				var sp = hostHeader.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (sp.Length <= 0)
				{
					goto InvalidRequest;
				}

				httpHeaders.Hostname = sp[0];

				if (sp.Length > 1)
				{
					if (!ushort.TryParse(sp[1], out var port))
					{
						goto InvalidRequest;
					}

					httpHeaders.Port = port;
				}
			}

			#endregion

			#region Content-Length

			if (!httpHeaders.IsConnect)
			{
				headers.TryGetValue(@"Content-Length", out var contentLengthStr);
				if (contentLengthStr is not null && int.TryParse(contentLengthStr, out var contentLength))
				{
					httpHeaders.ContentLength = contentLength;
				}
			}

			#endregion

			#region Request String

			if (!httpHeaders.IsConnect)
			{
				var request = new StringBuilder(8192);

				request.Append(httpHeaders.Method);
				request.Append(' ');
				request.Append(hostUri.PathAndQuery);
				request.Append(hostUri.Fragment);
				request.Append(' ');
				request.Append(httpHeaders.HttpVersion);
				request.Append(HttpNewLine);

				foreach (var (name, value) in headers)
				{
					request.Append(name);
					request.Append(':');
					request.Append(' ');
					request.Append(value);
					request.Append(HttpNewLine);
				}

				request.Append(HttpNewLine);
				httpHeaders.Request = request.ToString();
			}

			#endregion

			return true;
InvalidRequest:
			httpHeaders = null;
			return false;
		}

		private static bool TryReadContentLength(string raw, out int contentLength)
		{
			var headerLines = raw.Split(NewLines, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			foreach (var headerLine in headerLines.Skip(1))
			{
				var sp = headerLine.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (sp.Length != 2)
				{
					continue;
				}

				var headerName = sp[0];
				var headerValue = sp[1];

				if (headerName.Equals(@"Content-Length", StringComparison.OrdinalIgnoreCase)
					&& int.TryParse(headerValue, out contentLength))
				{
					return true;
				}
			}

			contentLength = 0;
			return false;
		}

		private static async ValueTask<string> ReadHttpHeadersAsync(PipeReader reader, CancellationToken cancellationToken = default)
		{
			while (true)
			{
				var result = await reader.ReadAsync(cancellationToken);
				var buffer = result.Buffer;
				try
				{
					if (TryReadHeaders(ref buffer, out var headers))
					{
						if (!buffer.IsEmpty)
						{
							reader.CancelPendingRead();
						}
						return headers;
					}

					if (result.IsCompleted)
					{
						break;
					}
				}
				finally
				{
					reader.AdvanceTo(buffer.Start, buffer.End);
				}
			}

			throw new InvalidDataException(@"Cannot read HTTP headers.");
		}

		private static bool TryReadHeaders(ref ReadOnlySequence<byte> sequence, [NotNullWhen(true)] out string? headers)
		{
			var reader = new SequenceReader<byte>(sequence);
			if (reader.TryReadTo(out ReadOnlySequence<byte> headerBuffer, HttpHeaderEnd))
			{
				sequence = sequence.Slice(reader.Consumed);
				headers = Encoding.UTF8.GetString(headerBuffer); // 不包括结尾的 \r\n\r\n
				return true;
			}

			headers = default;
			return false;
		}
	}
}