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

					if (httpHeaders.IsChunked)
					{
						await CopyChunkedRequestAsync(incomingPipe.Input, socks5Pipe.Output, cancellationToken);
					}
					else if (httpHeaders.ContentLength > 0)
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
					WriteFilteredResponse(responseHeaderBytes, output);
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

		// Copy remaining body to client until server closes the connection.
		// Since we force Connection: close in the outgoing request, the server
		// closes the connection after the full response regardless of framing
		// (Content-Length, chunked, or close-delimited).
		long readLength = await reader.CopyToAsync(output, long.MaxValue, cancellationToken);
		LogServerSentBytes(_logger, readLength);
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

	private static async ValueTask CopyChunkedRequestAsync(PipeReader reader, PipeWriter target, CancellationToken cancellationToken)
	{
		while (true)
		{
			ReadResult result = await reader.ReadAsync(cancellationToken);
			ReadOnlySequence<byte> buffer = result.Buffer;

			try
			{
				while (TryForwardChunk(ref buffer, target, out bool isLast))
				{
					if (isLast)
					{
						await target.FlushAsync(cancellationToken);
						return;
					}
				}

				await target.FlushAsync(cancellationToken);

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
	}

	private static bool TryForwardChunk(ref ReadOnlySequence<byte> buffer, PipeWriter target, out bool isLast)
	{
		isLast = false;
		SequenceReader<byte> seqReader = new(buffer);

		if (!seqReader.TryReadTo(out ReadOnlySequence<byte> chunkSizeLine, HttpNewLineSpan))
		{
			return false;
		}

		long lineConsumed = seqReader.Consumed; // includes \r\n
		long chunkSize = ParseChunkSize(chunkSizeLine);

		if (chunkSize < 0)
		{
			throw new InvalidDataException("Invalid chunk size: not a valid hexadecimal number.");
		}

		if (chunkSize == 0)
		{
			// Terminating chunk: "0[;ext]\r\n" followed by optional trailers and a final "\r\n".
			// Scan for the empty line that ends the trailer section.
			ReadOnlySequence<byte> afterLine = buffer.Slice(lineConsumed);
			SequenceReader<byte> trailerReader = new(afterLine);

			while (trailerReader.TryReadTo(out ReadOnlySequence<byte> trailerLine, HttpNewLineSpan))
			{
				if (trailerLine.Length == 0)
				{
					// Empty line = end of trailers
					long total = lineConsumed + trailerReader.Consumed;
					target.Write(buffer.Slice(0, total));
					buffer = buffer.Slice(total);
					isLast = true;
					return true;
				}
			}

			return false; // Need more data for trailers
		}

		// Non-zero chunk: chunk-size line (\r\n included) + data + \r\n
		long totalLen = lineConsumed + chunkSize + 2;

		if (buffer.Length < totalLen)
		{
			return false;
		}

		target.Write(buffer.Slice(0, totalLen));
		buffer = buffer.Slice(totalLen);
		return true;
	}

	private static long ParseChunkSize(ReadOnlySequence<byte> chunkSizeLine)
	{
		SequenceReader<byte> reader = new(chunkSizeLine);
		long length = 0;
		int digitCount = 0;

		while (reader.TryRead(out byte c))
		{
			if (c == (byte)';')
			{
				break; // Stop at chunk extension
			}

			int digit = c switch
			{
				>= (byte)'0' and <= (byte)'9' => c - '0',
				>= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
				>= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
				_ => -1,
			};

			if (digit < 0)
			{
				return -1; // Invalid character in chunk-size (RFC 7230 §4.1: chunk-size = 1*HEXDIG)
			}

			if (++digitCount > 16)
			{
				return -1; // Overflow: chunk size exceeds long.MaxValue (16 hex digits)
			}

			length = (length << 4) | (uint)digit;
		}

		return digitCount > 0 ? length : -1;
	}

}
