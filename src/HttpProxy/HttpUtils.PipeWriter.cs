using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Text;

namespace HttpProxy;

public static partial class HttpUtils
{
	extension(PipeWriter output)
	{
		/// <summary>
		/// Rewrites request line (absolute URI → relative path), filters hop-by-hop headers,
		/// appends Connection: close, writes directly to PipeWriter.
		/// <paramref name="headerBytes"/> must NOT include the trailing \r\n\r\n.
		/// </summary>
		internal void WriteFilteredRequest(ReadOnlySpan<byte> headerBytes)
		{
			// Parse request line: METHOD SP URI SP HTTP/X.Y \r\n
			int requestLineEnd = headerBytes.IndexOf(HttpNewLine);
			ReadOnlySpan<byte> requestLine = requestLineEnd < 0 ? headerBytes : headerBytes.Slice(0, requestLineEnd);
			ReadOnlySpan<byte> headerSection = requestLineEnd < 0 ? [] : headerBytes.Slice(requestLineEnd + 2);

			// Split request line into METHOD, URI, VERSION
			int firstSpace = requestLine.IndexOf((byte)' ');

			if (firstSpace < 0)
			{
				// Malformed — write as-is
				output.Write(headerBytes);
				output.Write(HttpHeaderEnd);
				return;
			}

			ReadOnlySpan<byte> method = requestLine.Slice(0, firstSpace);
			ReadOnlySpan<byte> rest = requestLine.Slice(firstSpace + 1);
			int secondSpace = rest.IndexOf((byte)' ');

			ReadOnlySpan<byte> uri;
			ReadOnlySpan<byte> version;

			if (secondSpace < 0)
			{
				uri = rest;
				version = "HTTP/1.1"u8;
			}
			else
			{
				uri = rest.Slice(0, secondSpace);
				version = rest.Slice(secondSpace + 1);
			}

			// Rewrite absolute URI to relative path
			ReadOnlySpan<byte> relativePath = ExtractRelativePath(uri);

			// Write rewritten request line
			output.Write(method);
			output.Write(" "u8);

			// origin-form requires path to start with '/'; query-only URIs return "?..."
			if (relativePath.Length > 0 && relativePath[0] is (byte)'?')
			{
				output.Write("/"u8);
			}

			output.Write(relativePath);
			output.Write(" "u8);
			output.Write(version);

			output.WriteFilteredHeaders(headerSection);

			output.Write(HttpNewLine);
			output.Write("Connection: close"u8);
			output.Write(HttpHeaderEnd);
		}

		/// <summary>
		/// Filters response headers and writes them to the PipeWriter.
		/// Returns <c>false</c> when the upstream response has invalid framing
		/// (e.g. unparseable or conflicting Content-Length without Transfer-Encoding),
		/// which per RFC 9112 §6.3 must be treated as a 502.
		/// The sequence must NOT include the trailing \r\n\r\n.
		/// </summary>
		internal bool WriteFilteredResponse(ReadOnlySequence<byte> headerBytes)
		{
			// Copy to contiguous buffer for simpler parsing — headers are typically small
			int len = (int)headerBytes.Length;
			byte[]? rented = null;
			Span<byte> buffer = len <= 4096
				? stackalloc byte[len]
				: (rented = ArrayPool<byte>.Shared.Rent(len)).AsSpan(0, len);

			try
			{
				headerBytes.CopyTo(buffer);

				// Status line
				int statusLineEnd = buffer.IndexOf(HttpNewLine);
				ReadOnlySpan<byte> statusLine = statusLineEnd < 0 ? buffer : buffer.Slice(0, statusLineEnd);
				ReadOnlySpan<byte> headerSection = statusLineEnd < 0 ? [] : buffer.Slice(statusLineEnd + 2);

				// RFC 9112 §4: status-line must start with "HTTP/"
				if (!statusLine.StartsWith("HTTP/"u8))
				{
					return false;
				}

				// RFC 9112 §6.3: reject before writing anything to the PipeWriter
				if (HasResponseFramingError(headerSection))
				{
					return false;
				}

				output.Write(statusLine);

				output.WriteFilteredHeaders(headerSection);

				output.Write(HttpNewLine);
				output.Write("Connection: close"u8);
				output.Write(HttpHeaderEnd);
				return true;
			}
			finally
			{
				if (rented is not null)
				{
					ArrayPool<byte>.Shared.Return(rented);
				}
			}
		}

		/// <summary>
		/// Two-pass header filter. First pass accumulates all Transfer-Encoding and Connection values
		/// (RFC 9110 §5.3: multiple same-name headers = comma-separated combined value).
		/// Second pass writes surviving headers and the combined Transfer-Encoding line.
		/// </summary>
		private void WriteFilteredHeaders(ReadOnlySpan<byte> headerSection)
		{
			long? contentLength = null;
			bool validContentLength = true;

			// First pass: accumulate all Transfer-Encoding and Connection values
			// RFC 9110 §5.3: multiple same-name headers = comma-separated combined value
			Span<byte> teBuf = stackalloc byte[32];
			byte[]? teRented = null;
			int teLen = 0;
			Span<byte> connBuf = stackalloc byte[32];
			byte[]? connRented = null;
			int connLen = 0;

			try
			{
				ReadOnlySpan<byte> remaining = headerSection;

				while (!remaining.IsEmpty)
				{
					int lineEnd = remaining.IndexOf(HttpNewLine);
					ReadOnlySpan<byte> line = lineEnd < 0 ? remaining : remaining.Slice(0, lineEnd);
					remaining = lineEnd < 0 ? [] : remaining.Slice(lineEnd + 2);

					if (line.IsEmpty)
					{
						continue;
					}

					int colon = line.IndexOf((byte)':');

					if (colon <= 0)
					{
						continue;
					}

					ReadOnlySpan<byte> name = line.Slice(0, colon).TrimEnd((byte)' ');
					ReadOnlySpan<byte> value = line.Slice(colon + 1).Trim((byte)' ');

					if (Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8))
					{
						AppendHeaderValue(ref teBuf, ref teRented, ref teLen, value);
					}
					else if (Ascii.EqualsIgnoreCase(name, "Connection"u8))
					{
						AppendHeaderValue(ref connBuf, ref connRented, ref connLen, value);
					}
					else if (validContentLength && Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
					{
						validContentLength = TryAccumulateContentLength(value, ref contentLength);
					}
				}

				ReadOnlySpan<byte> combinedTe = teBuf.Slice(0, teLen);
				ReadOnlySpan<byte> connectionValue = connBuf.Slice(0, connLen);

				// RFC 9112 §6.3 rule 3: If Transfer-Encoding is present, ignore Content-Length
				if (teLen > 0 || !validContentLength)
				{
					contentLength = null;
				}

				// Second pass: write headers, skipping hop-by-hop, Connection-nominated, and Content-Length
				// Content-Length is always stripped here and written back normalized after the loop.
				remaining = headerSection;

				while (!remaining.IsEmpty)
				{
					int lineEnd = remaining.IndexOf(HttpNewLine);
					ReadOnlySpan<byte> line = lineEnd < 0 ? remaining : remaining.Slice(0, lineEnd);
					remaining = lineEnd < 0 ? [] : remaining.Slice(lineEnd + 2);

					if (line.IsEmpty)
					{
						continue;
					}

					int colon = line.IndexOf((byte)':');

					if (colon <= 0)
					{
						continue;
					}

					ReadOnlySpan<byte> name = line.Slice(0, colon).TrimEnd((byte)' ');

					if (IsHopByHopHeader(name))
					{
						continue;
					}

					if (!connectionValue.IsEmpty && IsConnectionNominated(connectionValue, name))
					{
						continue;
					}

					if (Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
					{
						continue;
					}

					output.Write(HttpNewLine);
					output.Write(line);
				}

				// Write combined Transfer-Encoding header (preserve even when not chunked)
				if (teLen > 0)
				{
					output.Write(HttpNewLine);
					output.Write("Transfer-Encoding: "u8);
					output.Write(combinedTe);
				}

				// Write validated, deduplicated Content-Length (only when no TE present)
				if (teLen is 0 && contentLength is not null && validContentLength)
				{
					Span<byte> clBuf = stackalloc byte[20];
					Utf8Formatter.TryFormat(contentLength.Value, clBuf, out int clLen);
					output.Write(HttpNewLine);
					output.Write("Content-Length: "u8);
					output.Write(clBuf.Slice(0, clLen));
				}
			}
			finally
			{
				if (teRented is not null)
				{
					ArrayPool<byte>.Shared.Return(teRented);
				}

				if (connRented is not null)
				{
					ArrayPool<byte>.Shared.Return(connRented);
				}
			}
		}

		internal async ValueTask SendErrorAsync(ConnectionErrorResult error, CancellationToken cancellationToken = default)
		{
			switch (error)
			{
				case ConnectionErrorResult.AuthenticationError:
				{
					output.Write("HTTP/1.1 407 Proxy Authentication Required"u8);
					output.Write(HttpNewLine);
					output.Write("Proxy-Authenticate: Basic realm=\"proxy\""u8);
					break;
				}
				case ConnectionErrorResult.HostUnreachable:
				{
					output.Write("HTTP/1.1 502 Bad Gateway"u8);
					output.Write(HttpNewLine);
					output.Write("X-Proxy-Error-Type: HostUnreachable"u8);
					break;
				}
				case ConnectionErrorResult.ConnectionRefused:
				{
					output.Write("HTTP/1.1 502 Bad Gateway"u8);
					output.Write(HttpNewLine);
					output.Write("X-Proxy-Error-Type: ConnectionRefused"u8);
					break;
				}
				case ConnectionErrorResult.ConnectionReset:
				{
					output.Write("HTTP/1.1 502 Bad Gateway"u8);
					output.Write(HttpNewLine);
					output.Write("X-Proxy-Error-Type: ConnectionReset"u8);
					break;
				}
				case ConnectionErrorResult.InvalidResponse:
				{
					output.Write("HTTP/1.1 502 Bad Gateway"u8);
					output.Write(HttpNewLine);
					output.Write("X-Proxy-Error-Type: InvalidResponse"u8);
					break;
				}
				case ConnectionErrorResult.InvalidRequest:
				{
					output.Write("HTTP/1.1 400 Bad Request"u8);
					output.Write(HttpNewLine);
					output.Write("X-Proxy-Error-Type: InvalidRequest"u8);
					break;
				}
				case ConnectionErrorResult.UnknownError:
				default:
				{
					output.Write("HTTP/1.1 500 Internal Server Error"u8);
					output.Write(HttpNewLine);
					output.Write("X-Proxy-Error-Type: UnknownError"u8);
					break;
				}
			}

			output.Write(HttpNewLine);
			output.Write("Connection: close"u8);
			output.Write(HttpHeaderEnd);

			await output.FlushAsync(cancellationToken);
		}

		internal async ValueTask SendConnectSuccessAsync(CancellationToken cancellationToken = default)
		{
			output.Write("HTTP/1.1 200 Connection Established"u8);
			output.Write(HttpHeaderEnd);
			await output.FlushAsync(cancellationToken);
		}
	}

}
