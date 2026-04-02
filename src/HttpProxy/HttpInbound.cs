using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pipelines.Extensions;
using Proxy.Abstractions;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using static HttpProxy.HttpUtils;

namespace HttpProxy;

/// <summary>
/// Forwards HTTP requests through a configurable outbound connector, rewriting headers on the fly.
/// </summary>
/// <param name="credential">Optional proxy credentials. When set, clients must present matching Basic credentials.</param>
/// <param name="logger">Optional logger instance.</param>
public partial class HttpInbound(HttpProxyCredential? credential = null, ILogger<HttpInbound>? logger = null) : IStreamInbound
{
	private readonly byte[]? _expectedAuthBytes = credential is not null
		? Encoding.ASCII.GetBytes("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.UserName}:{credential.Password}")))
		: null;

	#region logging

	private readonly ILogger<HttpInbound> _logger = logger ?? NullLogger<HttpInbound>.Instance;

	[LoggerMessage(Level = LogLevel.Trace, Message = "Client headers received: \n{Headers}")]
	private partial void LogClientHeadersReceived(string headers);

	[LoggerMessage(Level = LogLevel.Debug, Message = "New request: Connect={IsConnect}, Host={Hostname}, Port={Port}, ContentLength={ContentLength}")]
	private partial void LogParsedRequest(bool isConnect, string hostname, ushort port, long? contentLength);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Waiting for up to {ContentLength} bytes from client")]
	private partial void LogWaitingForClientBytes(long contentLength);

	[LoggerMessage(Level = LogLevel.Debug, Message = "client sent {ClientSentLength} bytes to server")]
	private partial void LogClientSentBytes(long clientSentLength);

	[LoggerMessage(Level = LogLevel.Trace, Message = "server headers received: \n{Headers}")]
	private partial void LogServerHeadersReceived(string headers);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse HTTP request")]
	private partial void LogInvalidRequest();

	[LoggerMessage(Level = LogLevel.Warning, Message = "Connection to {Hostname}:{Port} failed")]
	private partial void LogConnectionFailed(string hostname, ushort port, Exception exception);

	[LoggerMessage(Level = LogLevel.Error, Message = "Outbound type {OutboundType} does not implement IStreamOutbound")]
	private partial void LogUnsupportedOutbound(string outboundType);

	[LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error forwarding HTTP request")]
	private partial void LogUnexpectedError(Exception exception);

	#endregion

	/// <summary>
	/// Reads an HTTP request from <paramref name="clientPipe"/>, connects to the target via
	/// <paramref name="outbound"/>, and relays the traffic.
	/// </summary>
	/// <param name="context">Per-connection metadata supplied by the accept loop.</param>
	/// <param name="clientPipe">The client-side duplex pipe.</param>
	/// <param name="outbound">The outbound connector used to reach the target host.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	public async ValueTask HandleAsync(InboundContext context, IDuplexPipe clientPipe, IOutbound outbound, CancellationToken cancellationToken = default)
	{
		try
		{
			byte[] rented;
			int headerLen;

			try
			{
				(rented, headerLen) = await ReadHeaderBytesAsync(clientPipe.Input, cancellationToken);
			}
			catch (InvalidDataException)
			{
				LogInvalidRequest();
				await clientPipe.Output.SendErrorAsync(ConnectionErrorResult.InvalidRequest, cancellationToken);
				return;
			}

			try
			{
				ReadOnlyMemory<byte> headerBytes = rented.AsMemory(0, headerLen);
				ReadOnlySpan<byte> headerSpan = headerBytes.Span;

				if (_logger.IsEnabled(LogLevel.Trace))
				{
					string header = Encoding.ASCII.GetString(headerSpan);
					LogClientHeadersReceived(header);
				}

				if (!TryParseHeaders(headerBytes, out HttpHeaders httpHeaders))
				{
					LogInvalidRequest();
					await clientPipe.Output.SendErrorAsync(ConnectionErrorResult.InvalidRequest, cancellationToken);
					return;
				}

				if (_expectedAuthBytes is not null &&
					!httpHeaders.ProxyAuthorization.Span.SequenceEqual(_expectedAuthBytes))
				{
					await clientPipe.Output.SendErrorAsync(ConnectionErrorResult.AuthenticationError, cancellationToken);
					return;
				}

				if (_logger.IsEnabled(LogLevel.Debug))
				{
					string hostname = Encoding.ASCII.GetString(httpHeaders.Hostname.Span);
					LogParsedRequest(httpHeaders.IsConnect, hostname, httpHeaders.Port, httpHeaders.ContentLength);
				}

				if (outbound is not IStreamOutbound streamOutbound)
				{
					Type outboundType = outbound.GetType();
					LogUnsupportedOutbound(outboundType.FullName ?? outboundType.Name);
					await clientPipe.Output.SendErrorAsync(ConnectionErrorResult.UnknownError, cancellationToken);
					return;
				}

				IConnection connection;

				try
				{
					connection = await streamOutbound.ConnectAsync(new ProxyDestination(httpHeaders.Hostname, httpHeaders.Port), cancellationToken);
				}
				catch (Exception ex)
				{
					string hostname = Encoding.ASCII.GetString(httpHeaders.Hostname.Span);
					LogConnectionFailed(hostname, httpHeaders.Port, ex);

					ConnectionErrorResult errorResult = ex is SocketException socketEx
						? socketEx.SocketErrorCode switch
						{
							SocketError.HostNotFound or SocketError.HostUnreachable => ConnectionErrorResult.HostUnreachable,
							SocketError.ConnectionRefused => ConnectionErrorResult.ConnectionRefused,
							SocketError.ConnectionReset => ConnectionErrorResult.ConnectionReset,
							_ => ConnectionErrorResult.UnknownError,
						}
						: ConnectionErrorResult.UnknownError;

					await clientPipe.Output.SendErrorAsync(errorResult, cancellationToken);
					return;
				}

				await using (connection)
				{
					if (httpHeaders.IsConnect)
					{
						await clientPipe.Output.SendConnectSuccessAsync(cancellationToken);
						await connection.LinkToAsync(clientPipe, cancellationToken);
					}
					else
					{
						connection.Output.WriteFilteredRequest(rented.AsSpan(0, headerLen));
						await connection.Output.FlushAsync(cancellationToken);

						// ContentLength: null=chunked, 0=no body, >0=fixed
						if (httpHeaders.ContentLength is null)
						{
							try
							{
								await CopyChunkedRequestAsync(clientPipe.Input, connection.Output, cancellationToken);
							}
							catch (InvalidDataException)
							{
								LogInvalidRequest();
								await clientPipe.Output.SendErrorAsync(ConnectionErrorResult.InvalidRequest, cancellationToken);
								return;
							}
						}
						else if (httpHeaders.ContentLength > 0)
						{
							LogWaitingForClientBytes(httpHeaders.ContentLength.Value);

							long readLength = await clientPipe.Input.CopyToAsync(connection.Output, httpHeaders.ContentLength.Value, cancellationToken: cancellationToken);

							LogClientSentBytes(readLength);

							if (readLength < httpHeaders.ContentLength.Value)
							{
								LogInvalidRequest();
								await clientPipe.Output.SendErrorAsync(ConnectionErrorResult.InvalidRequest, cancellationToken);
								return;
							}
						}

						if (!await ReadAndWriteFilteredResponseAsync(connection.Input, clientPipe.Output, cancellationToken))
						{
							await clientPipe.Output.SendErrorAsync(ConnectionErrorResult.InvalidResponse, cancellationToken);
						}
					}
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(rented);
			}
		}
		catch (Exception ex)
		{
			LogUnexpectedError(ex);

			try
			{
				await clientPipe.Output.SendErrorAsync(ConnectionErrorResult.UnknownError, cancellationToken);
			}
			catch
			{
				/* Don't mask the original exception */
			}

			throw;
		}
	}

	/// <summary>
	/// Returns <c>false</c> when the upstream response has a framing error (caller should send 502).
	/// </summary>
	private async ValueTask<bool> ReadAndWriteFilteredResponseAsync(PipeReader reader, PipeWriter output, CancellationToken cancellationToken)
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
						string header = Encoding.ASCII.GetString(responseHeaderBytes);
						LogServerHeadersReceived(header);
					}

					// WriteFilteredResponse copies to contiguous buffer internally, safe before AdvanceTo.
					// Returns false on response framing errors (RFC 9112 §6.3 → 502).
					if (!output.WriteFilteredResponse(responseHeaderBytes))
					{
						return false;
					}

					await output.FlushAsync(cancellationToken);

					// Leftover body bytes remain unexamined so the next ReadAsync returns them immediately.
					buffer = buffer.Slice(consumed, 0);

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
			return false;// Upstream closed before sending complete headers → 502
		}

		// Copy remaining body to client until server closes the connection.
		// Since we force Connection: close in the outgoing request, the server
		// closes the connection after the full response regardless of framing
		// (Content-Length, chunked, or close-delimited).
		await reader.CopyToAsync(output, cancellationToken);
		return true;
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

					// Leftover bytes remain unexamined so the caller's CopyToAsync sees them.
					buffer = buffer.Slice(consumed, 0);

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

	private static async ValueTask CopyChunkedRequestAsync(PipeReader reader, PipeWriter target, CancellationToken cancellationToken)
	{
		while (true)
		{
			ReadResult result = await reader.ReadAsync(cancellationToken);
			ReadOnlySequence<byte> buffer = result.Buffer;

			try
			{
				while (TryForwardChunk(ref buffer, out bool isLast))
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
					throw new InvalidDataException("Incomplete chunked request body.");
				}
			}
			finally
			{
				reader.AdvanceTo(buffer.Start, buffer.End);
			}
		}

		bool TryForwardChunk(ref ReadOnlySequence<byte> buffer, out bool isLast)
		{
			isLast = false;
			SequenceReader<byte> seqReader = new(buffer);

			if (!seqReader.TryReadTo(out ReadOnlySequence<byte> chunkSizeLine, HttpNewLine))
			{
				return false;
			}

			long lineConsumed = seqReader.Consumed;// includes \r\n

			if (!TryParseChunkSize(chunkSizeLine, out long chunkSize))
			{
				throw new InvalidDataException("Invalid chunk size: not a valid hexadecimal number.");
			}

			if (chunkSize is 0)
			{
				// Terminating chunk: "0[;ext]\r\n" followed by optional trailers and a final "\r\n".
				// Scan for the empty line that ends the trailer section.
				ReadOnlySequence<byte> afterLine = buffer.Slice(lineConsumed);
				SequenceReader<byte> trailerReader = new(afterLine);

				while (trailerReader.TryReadTo(out ReadOnlySequence<byte> trailerLine, HttpNewLine))
				{
					if (trailerLine.Length is 0)
					{
						// Empty line = end of trailers
						long total = lineConsumed + trailerReader.Consumed;
						target.Write(buffer.Slice(0, total));
						buffer = buffer.Slice(total);
						isLast = true;
						return true;
					}
				}

				return false;// Need more data for trailers
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
	}
}
