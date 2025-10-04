using HttpProxy.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pipelines.Extensions;
using Socks5.Clients;
using Socks5.Enums;
using Socks5.Exceptions;
using Socks5.Models;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text;
using static HttpProxy.HttpUtils;

namespace HttpProxy;

public class HttpToSocks5
{
	private readonly ILogger<HttpToSocks5> _logger;

	public HttpToSocks5(ILogger<HttpToSocks5>? logger = null)
	{
		_logger = logger ?? NullLogger<HttpToSocks5>.Instance;
	}

	public async ValueTask ForwardToSocks5Async(IDuplexPipe incomingPipe, Socks5CreateOption socks5CreateOption, CancellationToken cancellationToken = default)
	{
		try
		{
			string headers = await ReadHttpHeadersAsync(incomingPipe.Input, cancellationToken);
			_logger.LogDebug("Client headers received: \n{Headers}", headers);

			if (!TryParseHeader(headers, out HttpHeaders? httpHeaders))
			{
				await SendErrorAsync(incomingPipe.Output, ConnectionErrorResult.InvalidRequest, token: cancellationToken);
				return;
			}
			_logger.LogDebug("New request headers: \n{Headers}", httpHeaders);

			if (httpHeaders.Hostname is null || !httpHeaders.IsConnect && httpHeaders.Request is null)
			{
				await SendErrorAsync(incomingPipe.Output, ConnectionErrorResult.InvalidRequest, httpHeaders.HttpVersion, cancellationToken);
				return;
			}

			using Socks5Client socks5Client = new(socks5CreateOption);
			try
			{
				await socks5Client.ConnectAsync(httpHeaders.Hostname, httpHeaders.Port, cancellationToken);
			}
			catch (Socks5ProtocolErrorException ex) when (ex.Socks5Reply == Socks5Reply.ConnectionNotAllowed)
			{
				await SendErrorAsync(incomingPipe.Output, ConnectionErrorResult.AuthenticationError, httpHeaders.HttpVersion, cancellationToken);
				return;
			}
			catch (Socks5ProtocolErrorException ex) when (ex.Socks5Reply == Socks5Reply.HostUnreachable)
			{
				await SendErrorAsync(incomingPipe.Output, ConnectionErrorResult.HostUnreachable, httpHeaders.HttpVersion, cancellationToken);
				return;
			}
			catch (Socks5ProtocolErrorException ex) when (ex.Socks5Reply == Socks5Reply.ConnectionRefused)
			{
				await SendErrorAsync(incomingPipe.Output, ConnectionErrorResult.ConnectionRefused, httpHeaders.HttpVersion, cancellationToken);
				return;
			}
			catch (Socks5ProtocolErrorException)
			{
				await SendErrorAsync(incomingPipe.Output, ConnectionErrorResult.InvalidRequest, httpHeaders.HttpVersion, cancellationToken);
				return;
			}

			if (httpHeaders.IsConnect)
			{
				await SendConnectSuccessAsync(incomingPipe.Output, httpHeaders.HttpVersion, cancellationToken);

				IDuplexPipe socks5Pipe = socks5Client.GetPipe();

				await socks5Pipe.LinkToAsync(incomingPipe, cancellationToken);
			}
			else
			{
				IDuplexPipe socks5Pipe = socks5Client.GetPipe();

				await socks5Pipe.Output.WriteAsync(httpHeaders.Request!, cancellationToken);
				if (httpHeaders.ContentLength > 0)
				{
					_logger.LogDebug(@"Waiting for up to {ContentLength} bytes from client", httpHeaders.ContentLength);

					long readLength = await incomingPipe.Input.CopyToAsync(socks5Pipe.Output, httpHeaders.ContentLength, cancellationToken);

					_logger.LogDebug(@"client sent {ClientSentLength} bytes to server", readLength);
				}

				//Read response
				string responseHeadersStr = await ReadHttpHeadersAsync(socks5Pipe.Input, cancellationToken);
				_logger.LogDebug("server headers received: \n{Headers}", responseHeadersStr);
				Dictionary<string, string> responseHeaders = ReadHeaders(responseHeadersStr);

				incomingPipe.Output.Write(responseHeadersStr);
				incomingPipe.Output.Write(HttpHeaderEnd);
				await incomingPipe.Output.FlushAsync(cancellationToken);

				if (IsChunked(responseHeaders))
				{
					await CopyChunksAsync(socks5Pipe.Input, incomingPipe.Output, cancellationToken);
				}
				else if (TryReadContentLength(responseHeaders, out long serverResponseContentLength))
				{
					if (serverResponseContentLength > 0)
					{
						_logger.LogDebug(@"Waiting for up to {ContentLength} bytes from server", serverResponseContentLength);

						long readLength = await socks5Pipe.Input.CopyToAsync(incomingPipe.Output, serverResponseContentLength, cancellationToken);

						_logger.LogDebug(@"server sent {ServerSentLength} bytes to client", readLength);
					}
				}
			}
		}
		catch (Exception)
		{
			await SendErrorAsync(incomingPipe.Output, ConnectionErrorResult.UnknownError, token: cancellationToken);
			throw;
		}
	}

	private static async ValueTask SendErrorAsync(PipeWriter writer, ConnectionErrorResult error, string httpVersion = @"HTTP/1.1", CancellationToken token = default)
	{
		string str = BuildErrorResponse(error, httpVersion);
		await writer.WriteAsync(str, token);
	}

	private static async ValueTask SendConnectSuccessAsync(PipeWriter writer, string httpVersion = @"HTTP/1.1", CancellationToken token = default)
	{
		string str = $"{httpVersion} 200 Connection Established\r\n\r\n";
		await writer.WriteAsync(str, token);
	}

	private static bool TryParseHeader(string raw, [NotNullWhen(true)] out HttpHeaders? httpHeaders)
	{
		string[] headerLines = raw.SplitLines();

		if (headerLines.Length <= 0)
		{
			goto InvalidRequest;
		}

		string[] methodLine = headerLines[0].Split(' ');
		if (methodLine.Length is not 3) // METHOD URI HTTP/X.Y
		{
			goto InvalidRequest;
		}

		#region Read Headers

		Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
		foreach (string headerLine in headerLines.Skip(1))
		{
			string[] sp = headerLine.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (sp.Length != 2)
			{
				goto InvalidRequest;
			}

			string headerName = sp[0];
			string headerValue = sp[1];

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

		headers.TryGetValue(@"Host", out string? hostHeader);
		if (string.IsNullOrEmpty(hostHeader) && httpHeaders.IsConnect)
		{
			hostHeader = httpHeaders.HostUriString;
		}

		Uri hostUri = new(httpHeaders.HostUriString);
		if (string.IsNullOrEmpty(hostHeader))
		{
			httpHeaders.Hostname = hostUri.Host;
			httpHeaders.Port = (ushort)hostUri.Port;
		}
		else
		{
			string[] sp = hostHeader.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (sp.Length <= 0)
			{
				goto InvalidRequest;
			}

			httpHeaders.Hostname = sp[0];

			if (sp.Length > 1)
			{
				if (!ushort.TryParse(sp[1], out ushort port))
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
			headers.TryGetValue(@"Content-Length", out string? contentLengthStr);
			if (contentLengthStr is not null && long.TryParse(contentLengthStr, out long contentLength))
			{
				httpHeaders.ContentLength = contentLength;
			}
		}

		#endregion

		#region Request String

		if (!httpHeaders.IsConnect)
		{
			StringBuilder request = new(8192);

			request.Append(httpHeaders.Method);
			request.Append(' ');
			request.Append(hostUri.PathAndQuery);
			request.Append(hostUri.Fragment);
			request.Append(' ');
			request.Append(httpHeaders.HttpVersion);
			request.Append(HttpNewLine);

			foreach ((string name, string value) in headers)
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

	private static bool TryReadContentLength(IDictionary<string, string> headers, out long contentLength)
	{
		if (headers.TryGetValue(@"Content-Length", out string? value) && long.TryParse(value, out contentLength))
		{
			return true;
		}

		contentLength = 0;
		return false;
	}

	private static async ValueTask<string> ReadHttpHeadersAsync(PipeReader reader, CancellationToken cancellationToken = default)
	{
		while (true)
		{
			ReadResult result = await reader.ReadAsync(cancellationToken);
			ReadOnlySequence<byte> buffer = result.Buffer;
			try
			{
				if (TryReadHeaders(ref buffer, out string? headers))
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
		SequenceReader<byte> reader = new(sequence);
		if (reader.TryReadTo(out ReadOnlySequence<byte> headerBuffer, HttpHeaderEnd))
		{
			sequence = sequence.Slice(reader.Consumed);
			headers = Encoding.UTF8.GetString(headerBuffer); // 不包括结尾的 \r\n\r\n
			return true;
		}

		headers = default;
		return false;
	}

	private static Dictionary<string, string> ReadHeaders(string raw)
	{
		string[] headerLines = raw.SplitLines();
		Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
		foreach (string headerLine in headerLines.Skip(1))
		{
			string[] sp = headerLine.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (sp.Length != 2)
			{
				continue;
			}

			string headerName = sp[0];
			string headerValue = sp[1];

			headers[headerName] = headerValue;
		}
		return headers;
	}

	private static bool IsChunked(IDictionary<string, string> headers)
	{
		//Transfer-Encoding: chunked
		return headers.TryGetValue(@"Transfer-Encoding", out string? value) && value.Equals(@"chunked", StringComparison.OrdinalIgnoreCase);
	}

	private static async ValueTask CopyChunksAsync(
		PipeReader reader,
		PipeWriter target,
		CancellationToken cancellationToken = default)
	{
		while (true)
		{
			ReadResult result = await reader.ReadAsync(cancellationToken);
			ReadOnlySequence<byte> buffer = result.Buffer;

			try
			{
				while (true)
				{
					long lengthOfChunkLength = ReadLine(buffer, out ReadOnlySequence<byte> chunkLengthBuffer);
					if (lengthOfChunkLength <= 0)
					{
						break;
					}

					long chunkLength = GetChunkLength(chunkLengthBuffer);

					long length = lengthOfChunkLength + chunkLength + 2;
					if (buffer.Length < length)
					{
						break;
					}

					FlushResult flushResult = await target.WriteAsync(buffer.Slice(0, length), cancellationToken);
					buffer = buffer.Slice(length);

					if (flushResult.IsCompleted || chunkLength is 0L)
					{
						return;
					}
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

		static long GetChunkLength(ReadOnlySequence<byte> sequence)
		{
			SequenceReader<byte> reader = new(sequence);
			long length = 0L;
			while (reader.TryRead(out byte c))
			{
				length <<= 4;
				length += c > '9' ? (c > 'Z' ? (c - 'a' + 10) : (c - 'A' + 10)) : (c - '0');
			}
			return length;
		}

		static long ReadLine(ReadOnlySequence<byte> sequence, out ReadOnlySequence<byte> value)
		{
			SequenceReader<byte> reader = new(sequence);
			if (reader.TryReadTo(out value, HttpNewLineSpan))
			{
				// value 不包括结尾的 \r\n
			}

			return reader.Consumed;
		}
	}
}
