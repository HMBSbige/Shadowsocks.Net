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
		/// Single-pass: rewrites request line (absolute URI → relative path), filters hop-by-hop headers,
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
				ReadOnlySpan<byte> span = buffer;

				// Status line
				int statusLineEnd = span.IndexOf(HttpNewLine);
				ReadOnlySpan<byte> statusLine = statusLineEnd < 0 ? span : span.Slice(0, statusLineEnd);
				ReadOnlySpan<byte> headerSection = statusLineEnd < 0 ? [] : span.Slice(statusLineEnd + 2);

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
	}

	/// <summary>
	/// Given an absolute URI like "http://host/path?q=1", returns the relative part "/path?q=1".
	/// If no path is found after the authority, returns "/".
	/// </summary>
	private static ReadOnlySpan<byte> ExtractRelativePath(ReadOnlySpan<byte> absoluteUri)
	{
		// Origin-form starts with "/" — already relative, return as-is.
		if (absoluteUri.Length > 0 && absoluteUri[0] is (byte)'/')
		{
			return absoluteUri;
		}

		// Find "://" to locate scheme end in absolute-form URI.
		// If no scheme is found (e.g. bare hostname), return as-is — let the upstream handle it.
		int schemeEnd = absoluteUri.IndexOf("://"u8);

		if (schemeEnd < 0)
		{
			return absoluteUri;
		}

		ReadOnlySpan<byte> afterScheme = absoluteUri.Slice(schemeEnd + 3);

		// Find first '/' or '?' after authority (RFC 3986: path-empty + query is valid)
		int pos = afterScheme.IndexOfAny((byte)'/', (byte)'?');

		if (pos < 0)
		{
			return "/"u8;
		}

		// "/path..." or "?query" (caller prepends "/" when result starts with '?')
		return afterScheme.Slice(pos);
	}

	/// <summary>
	/// RFC 9112 §6.3 rule 5: invalid or conflicting Content-Length without
	/// Transfer-Encoding is an unrecoverable framing error for a proxy.
	/// </summary>
	private static bool HasResponseFramingError(ReadOnlySpan<byte> headerSection)
	{
		bool hasTransferEncoding = false;
		bool validContentLength = true;
		long? contentLength = null;

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
				hasTransferEncoding = true;
			}
			else if (validContentLength && Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
			{
				validContentLength = TryAccumulateContentLength(value, ref contentLength);
			}
		}

		// TE present → CL is ignored per §6.3 rule 3 (not a framing error).
		// No TE + invalid CL → unrecoverable framing error (§6.3 rule 5).
		return !hasTransferEncoding && !validContentLength;
	}

	/// <summary>
	/// Returns true if <paramref name="name"/> is a hop-by-hop header that should be removed
	/// before forwarding (RFC 9110 §7.6.1). Proxy-Authenticate and Proxy-Authorization are also
	/// removed to prevent credential leakage. Comparison is case-insensitive, ASCII-only.
	/// </summary>
	private static bool IsHopByHopHeader(ReadOnlySpan<byte> name)
	{
		return Ascii.EqualsIgnoreCase(name, "CONNECTION"u8)
				|| Ascii.EqualsIgnoreCase(name, "TRANSFER-ENCODING"u8)
				|| Ascii.EqualsIgnoreCase(name, "PROXY-AUTHORIZATION"u8)
				|| Ascii.EqualsIgnoreCase(name, "KEEP-ALIVE"u8)
				|| Ascii.EqualsIgnoreCase(name, "PROXY-AUTHENTICATE"u8)
				|| Ascii.EqualsIgnoreCase(name, "Proxy-Connection"u8)
				|| Ascii.EqualsIgnoreCase(name, "TE"u8)
				|| Ascii.EqualsIgnoreCase(name, "UPGRADE"u8);
	}

	/// <summary>
	/// Returns true if <paramref name="name"/> appears as a comma-separated token
	/// inside the Connection header <paramref name="connectionValue"/>.
	/// </summary>
	private static bool IsConnectionNominated(ReadOnlySpan<byte> connectionValue, ReadOnlySpan<byte> name)
	{
		while (!connectionValue.IsEmpty)
		{
			int comma = connectionValue.IndexOf((byte)',');
			ReadOnlySpan<byte> token = comma < 0 ? connectionValue : connectionValue.Slice(0, comma);
			token = token.Trim((byte)' ');

			if (Ascii.EqualsIgnoreCase(token, name))
			{
				return true;
			}

			connectionValue = comma < 0 ? [] : connectionValue.Slice(comma + 1);
		}

		return false;
	}

	/// <summary>
	/// Appends a header value to a growable buffer, joining with ", " for multi-line headers.
	/// Falls back to <see cref="ArrayPool{T}"/> when the buffer overflows.
	/// </summary>
	private static void AppendHeaderValue(ref Span<byte> buf, ref byte[]? rented, ref int len, ReadOnlySpan<byte> value)
	{
		int needed = len + 2 + value.Length;

		if (needed > buf.Length)
		{
			byte[] grown = ArrayPool<byte>.Shared.Rent(needed);
			buf.Slice(0, len).CopyTo(grown);

			if (rented is not null)
			{
				ArrayPool<byte>.Shared.Return(rented);
			}

			buf = rented = grown;
		}

		if (len > 0)
		{
			buf[len++] = (byte)',';
			buf[len++] = (byte)' ';
		}

		value.CopyTo(buf.Slice(len));
		len += value.Length;
	}
}
