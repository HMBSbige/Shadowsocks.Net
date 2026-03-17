using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Text;

namespace HttpProxy;

/// <summary>
/// Low-level HTTP header parsing and rewriting utilities operating directly on byte spans.
/// </summary>
public static class HttpUtils
{
	/// <summary>
	/// The byte sequence <c>\r\n\r\n</c> that terminates an HTTP header block.
	/// </summary>
	public static ReadOnlySpan<byte> HttpHeaderEnd => "\r\n\r\n"u8;

	/// <summary>
	/// The byte sequence <c>\r\n</c> used as a line separator in HTTP headers.
	/// </summary>
	public static ReadOnlySpan<byte> HttpNewLineSpan => "\r\n"u8;

	/// <summary>
	/// Checks whether the buffer starts with a valid HTTP request header.
	/// </summary>
	public static bool IsHttpHeader(ReadOnlySequence<byte> buffer)
	{
		SequenceReader<byte> reader = new(buffer);

		if (!reader.TryReadTo(out ReadOnlySequence<byte> headerBuffer, HttpHeaderEnd))
		{
			return false;
		}

		// Get request line (up to first \r\n, or entire headerBuffer if no additional headers)
		reader = new SequenceReader<byte>(headerBuffer);

		if (!reader.TryReadTo(out ReadOnlySequence<byte> requestLine, HttpNewLineSpan))
		{
			requestLine = headerBuffer;
		}

		// METHOD URI HTTP/X.Y → exactly 2 spaces
		reader = new SequenceReader<byte>(requestLine);

		return reader.TryReadTo(out ReadOnlySequence<byte> _, (byte)' ')
				&& reader.TryReadTo(out ReadOnlySequence<byte> _, (byte)' ')
				&& !reader.TryReadTo(out ReadOnlySequence<byte> _, (byte)' ');
	}

	/// <summary>
	/// Returns true if the last comma-separated token in the Transfer-Encoding value is "chunked".
	/// Per RFC 7230 §3.3.1, chunked must be the final encoding.
	/// </summary>
	private static bool HasChunkedEncoding(ReadOnlySpan<byte> value)
	{
		int lastComma = value.LastIndexOf((byte)',');
		ReadOnlySpan<byte> lastToken = lastComma < 0 ? value : value.Slice(lastComma + 1);
		return Ascii.EqualsIgnoreCase(lastToken.Trim((byte)' '), "chunked"u8);
	}

	/// <summary>
	/// Returns true if <paramref name="name"/> is one of the 9 standard hop-by-hop headers.
	/// Comparison is case-insensitive, ASCII-only.
	/// </summary>
	private static bool IsHopByHopHeader(ReadOnlySpan<byte> name)
	{
		return name.Length switch
		{
			2 => Ascii.EqualsIgnoreCase(name, "TE"u8),
			7 => Ascii.EqualsIgnoreCase(name, "UPGRADE"u8) || Ascii.EqualsIgnoreCase(name, "TRAILER"u8),
			10 => Ascii.EqualsIgnoreCase(name, "CONNECTION"u8) || Ascii.EqualsIgnoreCase(name, "KEEP-ALIVE"u8),
			17 => Ascii.EqualsIgnoreCase(name, "TRANSFER-ENCODING"u8),
			16 => Ascii.EqualsIgnoreCase(name, "Proxy-Connection"u8),
			18 => Ascii.EqualsIgnoreCase(name, "PROXY-AUTHENTICATE"u8),
			19 => Ascii.EqualsIgnoreCase(name, "PROXY-AUTHORIZATION"u8),
			_ => false,
		};
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
	/// Parses an authority (host[:port]) from bytes. Handles IPv4, IPv6 ([::1]:port), and hostnames.
	/// Returns false if the authority is empty or malformed.
	/// </summary>
	private static bool TryParseAuthority(ReadOnlySpan<byte> authority, out ReadOnlySpan<byte> host, out ushort port)
	{
		port = 0;
		host = default;

		authority = authority.Trim((byte)' ');

		if (authority.IsEmpty)
		{
			return false;
		}

		if (authority[0] == (byte)'[')
		{
			// IPv6: [::1]:port
			int closeBracket = authority.IndexOf((byte)']');

			if (closeBracket < 0)
			{
				return false;
			}

			host = authority.Slice(1, closeBracket - 1);

			ReadOnlySpan<byte> rest = authority.Slice(closeBracket + 1);

			if (!rest.IsEmpty)
			{
				if (rest[0] != (byte)':')
				{
					return false;
				}

				if (!(Utf8Parser.TryParse(rest.Slice(1), out port, out int consumed1) && consumed1 == rest.Length - 1))
				{
					return false;
				}
			}
		}
		else
		{
			// host:port or host — find last colon (not first, to avoid issues with bare IPv6 without brackets,
			// though that's technically invalid in HTTP)
			int colon = authority.LastIndexOf((byte)':');

			if (colon < 0)
			{
				host = authority;
			}
			else
			{
				ReadOnlySpan<byte> potentialPort = authority.Slice(colon + 1);

				if (Utf8Parser.TryParse(potentialPort, out port, out int consumed2) && consumed2 == potentialPort.Length)
				{
					host = authority.Slice(0, colon);
				}
				else
				{
					// No valid port — treat entire thing as host
					host = authority;
					port = 0;
				}
			}
		}

		return !host.IsEmpty;
	}

	/// <summary>
	/// Given an absolute URI like "http://host/path?q=1", returns the relative part "/path?q=1".
	/// If no path is found after the authority, returns "/".
	/// </summary>
	private static ReadOnlySpan<byte> ExtractRelativePath(ReadOnlySpan<byte> absoluteUri)
	{
		// Find "://"
		int schemeEnd = absoluteUri.IndexOf("://"u8);

		if (schemeEnd < 0)
		{
			return absoluteUri;
		}

		ReadOnlySpan<byte> afterScheme = absoluteUri.Slice(schemeEnd + 3);

		// Find first '/' after authority
		int slash = afterScheme.IndexOf((byte)'/');

		if (slash < 0)
		{
			return "/"u8;
		}

		return afterScheme.Slice(slash);
	}

	/// <summary>
	/// Single-pass: rewrites request line (absolute URI → relative path), filters hop-by-hop headers,
	/// appends Connection: close, writes directly to PipeWriter.
	/// <paramref name="headerBytes"/> must NOT include the trailing \r\n\r\n.
	/// </summary>
	public static void WriteFilteredRequest(ReadOnlySpan<byte> headerBytes, PipeWriter output)
	{
		// Parse request line: METHOD SP URI SP HTTP/X.Y \r\n
		int requestLineEnd = headerBytes.IndexOf("\r\n"u8);
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
		output.Write(relativePath);
		output.Write(" "u8);
		output.Write(version);

		WriteFilteredHeaders(headerSection, output);

		output.Write("\r\nConnection: close"u8);
		output.Write(HttpHeaderEnd);
	}

	/// <summary>
	/// Single-pass response header filter. Reads from a <see cref="ReadOnlySequence{T}"/> (PipeReader buffer),
	/// writes status line + filtered headers directly to <paramref name="output"/>.
	/// The sequence must NOT include the trailing \r\n\r\n.
	/// </summary>
	public static void WriteFilteredResponse(ReadOnlySequence<byte> headerBytes, PipeWriter output)
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
			int statusLineEnd = span.IndexOf("\r\n"u8);
			ReadOnlySpan<byte> statusLine = statusLineEnd < 0 ? span : span.Slice(0, statusLineEnd);
			ReadOnlySpan<byte> headerSection = statusLineEnd < 0 ? [] : span.Slice(statusLineEnd + 2);

			output.Write(statusLine);

			WriteFilteredHeaders(headerSection, output);

			output.Write(HttpHeaderEnd);
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
	/// (RFC 7230 §3.2.2: multiple same-name headers = comma-separated combined value).
	/// Second pass writes surviving headers and the combined Transfer-Encoding line.
	/// </summary>
	private static void WriteFilteredHeaders(
		ReadOnlySpan<byte> headerSection,
		PipeWriter output)
	{
		long contentLength = 0;
		bool hasContentLength = false;
		bool validContentLength = true;

		// First pass: accumulate all Transfer-Encoding and Connection values
		// RFC 7230 §3.2.2: multiple same-name headers = comma-separated combined value
		Span<byte> teBuf = stackalloc byte[256];
		byte[]? teRented = null;
		int teLen = 0;
		Span<byte> connBuf = stackalloc byte[512];
		byte[]? connRented = null;
		int connLen = 0;

		try
		{
			ReadOnlySpan<byte> remaining = headerSection;

			while (!remaining.IsEmpty)
			{
				int lineEnd = remaining.IndexOf("\r\n"u8);
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
				else if (Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
				{
					if (!TryAccumulateContentLength(value, ref contentLength, ref hasContentLength))
					{
						validContentLength = false;
					}
				}
			}

			ReadOnlySpan<byte> combinedTe = teBuf.Slice(0, teLen);
			ReadOnlySpan<byte> connectionValue = connBuf.Slice(0, connLen);

			// RFC 7230 §3.3.3: If Transfer-Encoding is present, ignore Content-Length
			if (teLen > 0 || !validContentLength)
			{
				contentLength = 0;
			}

			// Second pass: write headers, skipping hop-by-hop, Connection-nominated, and Content-Length
			// Content-Length is always stripped here and written back normalized after the loop.
			remaining = headerSection;

			while (!remaining.IsEmpty)
			{
				int lineEnd = remaining.IndexOf("\r\n"u8);
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

				output.Write(HttpNewLineSpan);
				output.Write(line);
			}

			// Write combined Transfer-Encoding header (preserve even when not chunked)
			if (teLen > 0)
			{
				output.Write("\r\nTransfer-Encoding: "u8);
				output.Write(combinedTe);
			}

			// Write validated, deduplicated Content-Length (only when no TE present)
			if (teLen == 0 && hasContentLength && validContentLength)
			{
				Span<byte> clBuf = stackalloc byte[20];
				Utf8Formatter.TryFormat(contentLength, clBuf, out int clLen);
				output.Write("\r\nContent-Length: "u8);
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

	/// <summary>
	/// Accumulates a Content-Length header value, detecting invalid formats and conflicting values
	/// per RFC 7230 §3.3.2 and §3.3.3.
	/// </summary>
	/// <returns>
	/// <see langword="true"/> if the value is valid and consistent with previous values;
	/// <see langword="false"/> if malformed or conflicting.
	/// </returns>
	private static bool TryAccumulateContentLength(
		ReadOnlySpan<byte> value,
		ref long contentLength,
		ref bool hasContentLength)
	{
		if (!Utf8Parser.TryParse(value, out long cl, out int consumed) || consumed != value.Length)
		{
			return false;
		}

		if (hasContentLength && contentLength != cl)
		{
			return false;
		}

		contentLength = cl;
		hasContentLength = true;
		return true;
	}

	/// <summary>
	/// Appends a header value to a growable buffer, joining with ", " for multi-line headers.
	/// Starts with a stackalloc'd span; falls back to <see cref="ArrayPool{T}"/> on overflow.
	/// </summary>
	private static void AppendHeaderValue(ref Span<byte> buf, ref byte[]? rented, ref int len, ReadOnlySpan<byte> value)
	{
		int needed = len + (len > 0 ? 2 : 0) + value.Length;

		if (needed > buf.Length)
		{
			byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(needed, buf.Length * 2));
			buf.Slice(0, len).CopyTo(grown);

			if (rented is not null)
			{
				ArrayPool<byte>.Shared.Return(rented);
			}

			rented = grown;
			buf = rented;
		}

		if (len > 0)
		{
			", "u8.CopyTo(buf.Slice(len));
			len += 2;
		}

		value.CopyTo(buf.Slice(len));
		len += value.Length;
	}

	/// <summary>
	/// Parses HTTP headers from raw bytes (without trailing \r\n\r\n) and extracts metadata.
	/// Returns false if the request line is malformed or the authority cannot be parsed.
	/// For non-CONNECT requests, the full header bytes must be provided to later write
	/// the filtered request via <see cref="WriteFilteredRequest"/>.
	/// </summary>
	internal static bool TryParseHeaders(ReadOnlySpan<byte> headerBytes, out HttpHeaders result)
	{
		result = default;

		// Request line: METHOD SP URI SP HTTP/X.Y
		int requestLineEnd = headerBytes.IndexOf("\r\n"u8);
		ReadOnlySpan<byte> requestLine = requestLineEnd < 0 ? headerBytes : headerBytes.Slice(0, requestLineEnd);
		ReadOnlySpan<byte> headerSection = requestLineEnd < 0 ? [] : headerBytes.Slice(requestLineEnd + 2);

		int firstSpace = requestLine.IndexOf((byte)' ');

		if (firstSpace < 0)
		{
			return false;
		}

		ReadOnlySpan<byte> method = requestLine.Slice(0, firstSpace);
		ReadOnlySpan<byte> rest = requestLine.Slice(firstSpace + 1);
		int secondSpace = rest.IndexOf((byte)' ');

		if (secondSpace < 0)
		{
			return false;
		}

		ReadOnlySpan<byte> uri = rest.Slice(0, secondSpace);

		bool isConnect = Ascii.EqualsIgnoreCase(method, "CONNECT"u8);

		// Single pass: extract Host, Content-Length, and Transfer-Encoding header values
		ReadOnlySpan<byte> hostValue = default;
		long contentLength = 0;
		bool hasContentLength = false;
		bool isChunked = false;
		bool hasTransferEncoding = false;
		ReadOnlySpan<byte> scan = headerSection;

		while (!scan.IsEmpty)
		{
			int lineEnd = scan.IndexOf("\r\n"u8);
			ReadOnlySpan<byte> line = lineEnd < 0 ? scan : scan.Slice(0, lineEnd);
			scan = lineEnd < 0 ? [] : scan.Slice(lineEnd + 2);

			int colon = line.IndexOf((byte)':');

			if (colon <= 0)
			{
				continue;
			}

			ReadOnlySpan<byte> name = line.Slice(0, colon).TrimEnd((byte)' ');

			if (Ascii.EqualsIgnoreCase(name, "Host"u8))
			{
				hostValue = line.Slice(colon + 1).Trim((byte)' ');
			}
			else if (!isConnect && Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
			{
				if (!TryAccumulateContentLength(line.Slice(colon + 1).Trim((byte)' '), ref contentLength, ref hasContentLength))
				{
					return false;
				}
			}
			else if (!isConnect && Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8))
			{
				hasTransferEncoding = true;
				isChunked = HasChunkedEncoding(line.Slice(colon + 1).Trim((byte)' '));
			}
		}

		// RFC 7230 §3.3.1: For requests, chunked must be the final transfer coding.
		// If TE is present but chunked is not last, body length cannot be determined.
		if (hasTransferEncoding && !isChunked)
		{
			return false;
		}

		// RFC 7230 §3.3.3: If Transfer-Encoding is present, ignore Content-Length
		if (hasTransferEncoding)
		{
			contentLength = 0;
		}

		ReadOnlySpan<byte> authority;
		ushort defaultPort = 80;

		if (isConnect)
		{
			// CONNECT host:port HTTP/1.1 — URI is the authority
			authority = !hostValue.IsEmpty ? hostValue : uri;
			defaultPort = 443;
		}
		else
		{
			if (!hostValue.IsEmpty)
			{
				authority = hostValue;
			}
			else
			{
				// Extract authority from absolute URI: http://host:port/path
				authority = ExtractAuthority(uri);
			}

			// Determine default port from scheme
			if (uri.Length > 8 && Ascii.EqualsIgnoreCase(uri.Slice(0, 8), "https://"u8))
			{
				defaultPort = 443;
			}
		}

		if (!TryParseAuthority(authority, out ReadOnlySpan<byte> host, out ushort port))
		{
			return false;
		}

		if (port == 0)
		{
			port = defaultPort;
		}

		string hostname = Encoding.Latin1.GetString(host);
		result = new HttpHeaders(isConnect, hostname, port, contentLength, isChunked);
		return true;
	}

	/// <summary>
	/// Extracts the authority portion from an absolute URI (e.g., "http://host:port/path" → "host:port").
	/// </summary>
	private static ReadOnlySpan<byte> ExtractAuthority(ReadOnlySpan<byte> absoluteUri)
	{
		int schemeEnd = absoluteUri.IndexOf("://"u8);

		if (schemeEnd < 0)
		{
			return absoluteUri;
		}

		ReadOnlySpan<byte> afterScheme = absoluteUri.Slice(schemeEnd + 3);

		int slash = afterScheme.IndexOf((byte)'/');

		return slash < 0 ? afterScheme : afterScheme.Slice(0, slash);
	}
}
