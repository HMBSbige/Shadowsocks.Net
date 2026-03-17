using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pipelines.Extensions;
using Socks5.Clients;
using Socks5.Enums;
using Socks5.Exceptions;
using Socks5.Models;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using static HttpProxy.HttpUtils;

namespace HttpProxy;

/// <summary>
/// Forwards HTTP requests through a SOCKS5 proxy, rewriting headers on the fly.
/// </summary>
/// <param name="logger">Optional logger instance.</param>
public partial class HttpToSocks5(ILogger<HttpToSocks5>? logger = null)
{
	#region logging

	private readonly ILogger<HttpToSocks5> _logger = logger ?? NullLogger<HttpToSocks5>.Instance;

	[LoggerMessage(Level = LogLevel.Trace, Message = "Client headers received: \n{Headers}")]
	private static partial void LogClientHeadersReceived(ILogger logger, string headers);

	[LoggerMessage(Level = LogLevel.Debug, Message = "New request: {Headers}")]
	private static partial void LogParsedRequestHeaders(ILogger logger, HttpHeaders headers);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Waiting for up to {ContentLength} bytes from client")]
	private static partial void LogWaitingForClientBytes(ILogger logger, long contentLength);

	[LoggerMessage(Level = LogLevel.Debug, Message = "client sent {ClientSentLength} bytes to server")]
	private static partial void LogClientSentBytes(ILogger logger, long clientSentLength);

	[LoggerMessage(Level = LogLevel.Trace, Message = "server headers received: \n{Headers}")]
	private static partial void LogServerHeadersReceived(ILogger logger, string headers);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Waiting for up to {ContentLength} bytes from server")]
	private static partial void LogWaitingForServerBytes(ILogger logger, long contentLength);

	[LoggerMessage(Level = LogLevel.Debug, Message = "server sent {ServerSentLength} bytes to client")]
	private static partial void LogServerSentBytes(ILogger logger, long serverSentLength);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse HTTP request")]
	private static partial void LogInvalidRequest(ILogger logger);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Incomplete HTTP request from client")]
	private static partial void LogIncompleteRequest(ILogger logger);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Socks5 connection to {Hostname}:{Port} was rejected ({Reply})")]
	private static partial void LogSocks5ConnectionRejected(ILogger logger, string? hostname, ushort port, Socks5Reply reply);

	[LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error forwarding HTTP request")]
	private static partial void LogUnexpectedError(ILogger logger, Exception exception);

	#endregion

	/// <summary>
	/// Reads an HTTP request from <paramref name="incomingPipe"/>, connects to the target via SOCKS5, and relays the traffic.
	/// </summary>
	/// <param name="incomingPipe">The client-side duplex pipe.</param>
	/// <param name="socks5CreateOption">Options for creating the SOCKS5 connection.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	public async ValueTask ForwardToSocks5Async(IDuplexPipe incomingPipe, Socks5CreateOption socks5CreateOption, CancellationToken cancellationToken = default)
	{
		try
		{
			(byte[] rented, int headerLen) = await ReadHeaderBytesAsync(incomingPipe.Input, cancellationToken);

			try
			{
				ReadOnlySpan<byte> headerSpan = rented.AsSpan(0, headerLen);

				if (_logger.IsEnabled(LogLevel.Trace))
				{
					LogClientHeadersReceived(_logger, Encoding.Latin1.GetString(headerSpan));
				}

				if (!TryParseHeaders(headerSpan, out HttpHeaders httpHeaders))
				{
					LogInvalidRequest(_logger);
					await SendErrorAsync(incomingPipe.Output, ConnectionErrorResult.InvalidRequest, cancellationToken);
					return;
				}

				LogParsedRequestHeaders(_logger, httpHeaders);

				using Socks5Client socks5Client = new(socks5CreateOption);

				try
				{
					await socks5Client.ConnectAsync(httpHeaders.Hostname, httpHeaders.Port, cancellationToken);
				}
				catch (Socks5ProtocolErrorException ex)
				{
					LogSocks5ConnectionRejected(_logger, httpHeaders.Hostname, httpHeaders.Port, ex.Socks5Reply);

					ConnectionErrorResult errorResult = ex.Socks5Reply switch
					{
						Socks5Reply.ConnectionNotAllowed => ConnectionErrorResult.AuthenticationError,
						Socks5Reply.HostUnreachable => ConnectionErrorResult.HostUnreachable,
						Socks5Reply.ConnectionRefused => ConnectionErrorResult.ConnectionRefused,
						_ => ConnectionErrorResult.InvalidRequest,
					};

					await SendErrorAsync(incomingPipe.Output, errorResult, cancellationToken);
					return;
				}

				if (httpHeaders.IsConnect)
				{
					await SendConnectSuccessAsync(incomingPipe.Output, cancellationToken);

					IDuplexPipe socks5Pipe = socks5Client.GetPipe();
					await socks5Pipe.LinkToAsync(incomingPipe, cancellationToken);
				}
				else
				{
					IDuplexPipe socks5Pipe = socks5Client.GetPipe();

					WriteFilteredRequest(rented.AsSpan(0, headerLen), socks5Pipe.Output);
					await socks5Pipe.Output.FlushAsync(cancellationToken);

					if (httpHeaders.ContentLength > 0)
					{
						LogWaitingForClientBytes(_logger, httpHeaders.ContentLength);

						long readLength = await incomingPipe.Input.CopyToAsync(socks5Pipe.Output, httpHeaders.ContentLength, cancellationToken);

						LogClientSentBytes(_logger, readLength);
					}

					await ReadAndWriteFilteredResponseAsync(socks5Pipe.Input, incomingPipe.Output, cancellationToken);
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(rented);
			}
		}
		catch (Exception ex)
		{
			LogUnexpectedError(_logger, ex);
			await SendErrorAsync(incomingPipe.Output, ConnectionErrorResult.UnknownError, cancellationToken);
			throw;
		}
	}

	private async ValueTask ReadAndWriteFilteredResponseAsync(PipeReader reader, PipeWriter output, CancellationToken cancellationToken)
	{
		bool isChunked = false;
		long serverContentLength = 0;
		bool headersDone = false;

		while (!headersDone)
		{
			ReadResult result = await reader.ReadAsync(cancellationToken);
			ReadOnlySequence<byte> buffer = result.Buffer;

			try
			{
				if (TryFindHeaderEnd(buffer, out ReadOnlySequence<byte> responseHeaderBytes, out long consumed))
				{
					if (_logger.IsEnabled(LogLevel.Trace))
					{
						LogServerHeadersReceived(_logger, Encoding.Latin1.GetString(responseHeaderBytes));
					}

					// WriteFilteredResponse copies to contiguous buffer internally, safe before AdvanceTo
					WriteFilteredResponse(responseHeaderBytes, output, out isChunked, out serverContentLength);
					await output.FlushAsync(cancellationToken);

					buffer = buffer.Slice(consumed);

					if (!buffer.IsEmpty)
					{
						reader.CancelPendingRead();
					}

					headersDone = true;
				}
				else if (result.IsCompleted)
				{
					break;
				}
			}
			finally
			{
				reader.AdvanceTo(buffer.Start, buffer.End);
			}
		}

		if (!headersDone)
		{
			throw new InvalidDataException("Cannot read HTTP response headers.");
		}

		if (isChunked)
		{
			await CopyChunksAsync(reader, output, cancellationToken);
		}
		else if (serverContentLength > 0)
		{
			LogWaitingForServerBytes(_logger, serverContentLength);

			long readLength = await reader.CopyToAsync(output, serverContentLength, cancellationToken);

			LogServerSentBytes(_logger, readLength);
		}
	}

	private static bool TryFindHeaderEnd(ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> headerBytes, out long consumed)
	{
		SequenceReader<byte> seqReader = new(buffer);

		if (seqReader.TryReadTo(out headerBytes, HttpHeaderEnd))
		{
			consumed = seqReader.Consumed;
			return true;
		}

		consumed = 0;
		return false;
	}

	/// <summary>
	/// Reads from PipeReader until the full header block (\r\n\r\n) is found.
	/// Returns a rented byte array containing header bytes WITHOUT the trailing \r\n\r\n.
	/// Caller must return the array to <see cref="ArrayPool{T}.Shared"/>.
	/// </summary>
	private static async ValueTask<(byte[] Buffer, int Length)> ReadHeaderBytesAsync(PipeReader reader, CancellationToken cancellationToken)
	{
		while (true)
		{
			ReadResult result = await reader.ReadAsync(cancellationToken);
			ReadOnlySequence<byte> buffer = result.Buffer;

			try
			{
				if (TryFindHeaderEnd(buffer, out ReadOnlySequence<byte> headerBuffer, out long consumed))
				{
					// Copy before AdvanceTo (in finally) invalidates the buffer
					int len = (int)headerBuffer.Length;
					byte[] rented = ArrayPool<byte>.Shared.Rent(len);
					headerBuffer.CopyTo(rented);

					// Slice buffer to remaining data; finally will AdvanceTo this position
					buffer = buffer.Slice(consumed);

					if (!buffer.IsEmpty)
					{
						reader.CancelPendingRead();
					}

					return (rented, len);
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

		throw new InvalidDataException("Cannot read HTTP headers.");
	}

	private static async ValueTask SendErrorAsync(PipeWriter writer, ConnectionErrorResult error, CancellationToken cancellationToken = default)
	{
		switch (error)
		{
			case ConnectionErrorResult.AuthenticationError:
			{
				writer.Write("HTTP/1.1 401 Unauthorized\r\n\r\n"u8);
				break;
			}
			case ConnectionErrorResult.HostUnreachable:
			{
				writer.Write("HTTP/1.1 502 HostUnreachable\r\n\r\n"u8);
				break;
			}
			case ConnectionErrorResult.ConnectionRefused:
			{
				writer.Write("HTTP/1.1 502 ConnectionRefused\r\n\r\n"u8);
				break;
			}
			case ConnectionErrorResult.ConnectionReset:
			{
				writer.Write("HTTP/1.1 502 ConnectionReset\r\n\r\n"u8);
				break;
			}
			case ConnectionErrorResult.InvalidRequest:
			{
				writer.Write("HTTP/1.1 500 Internal Server Error\r\nX-Proxy-Error-Type: InvalidRequest\r\n\r\n"u8);
				break;
			}
			case ConnectionErrorResult.UnknownError:
			default:
			{
				writer.Write("HTTP/1.1 500 Internal Server Error\r\nX-Proxy-Error-Type: UnknownError\r\n\r\n"u8);
				break;
			}
		}

		await writer.FlushAsync(cancellationToken);
	}

	private static async ValueTask SendConnectSuccessAsync(PipeWriter writer, CancellationToken cancellationToken = default)
	{
		writer.Write("HTTP/1.1 200 Connection Established\r\n\r\n"u8);
		await writer.FlushAsync(cancellationToken);
	}

	private static async ValueTask CopyChunksAsync(PipeReader reader, PipeWriter target, CancellationToken cancellationToken = default)
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

		return;

		static long GetChunkLength(ReadOnlySequence<byte> sequence)
		{
			SequenceReader<byte> reader = new(sequence);
			long length = 0L;

			while (reader.TryRead(out byte c))
			{
				length <<= 4;
				length += c > '9'
					? c > 'Z'
						? c - 'a' + 10
						: c - 'A' + 10
					: c - '0';
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
