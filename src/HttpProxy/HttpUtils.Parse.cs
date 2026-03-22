using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace HttpProxy;

public static partial class HttpUtils
{
	/// <summary>
	/// Parses HTTP headers from raw bytes (without trailing \r\n\r\n) and extracts metadata.
	/// Returns false if the request line is malformed or the authority cannot be parsed.
	/// For non-CONNECT requests, the full header bytes must be provided to later write
	/// the filtered request via <see cref="WriteFilteredRequest"/>.
	/// </summary>
	internal static bool TryParseHeaders(ReadOnlyMemory<byte> headerBytes, out HttpHeaders result)
	{
		result = default;
		ReadOnlySpan<byte> headerSpan = headerBytes.Span;

		// Request line: METHOD SP URI SP HTTP/X.Y
		int requestLineEnd = headerSpan.IndexOf(HttpNewLine);
		ReadOnlySpan<byte> requestLine = requestLineEnd < 0 ? headerSpan : headerSpan.Slice(0, requestLineEnd);
		ReadOnlyMemory<byte> headerSection = requestLineEnd < 0 ? ReadOnlyMemory<byte>.Empty : headerBytes.Slice(requestLineEnd + 2);

		int firstSpace = requestLine.IndexOf((byte)' ');

		if (firstSpace < 0)
		{
			return false;
		}

		int secondSpace = requestLine.Slice(firstSpace + 1).IndexOf((byte)' ');

		if (secondSpace < 0)
		{
			return false;
		}

		bool isConnect = Ascii.EqualsIgnoreCase(requestLine.Slice(0, firstSpace), "CONNECT"u8);
		ReadOnlyMemory<byte> uri = headerBytes.Slice(firstSpace + 1, secondSpace);

		// Single pass: extract Host, Content-Length, Transfer-Encoding, and Proxy-Authorization header values
		ReadOnlyMemory<byte> hostValue = default;
		long? contentLength = null;
		bool? isChunked = null;
		ReadOnlyMemory<byte> proxyAuth = default;
		ReadOnlyMemory<byte> scan = headerSection;

		while (!scan.IsEmpty)
		{
			int lineEnd = scan.Span.IndexOf(HttpNewLine);
			ReadOnlyMemory<byte> line = lineEnd < 0 ? scan : scan.Slice(0, lineEnd);
			scan = lineEnd < 0 ? ReadOnlyMemory<byte>.Empty : scan.Slice(lineEnd + 2);
			ReadOnlySpan<byte> lineSpan = line.Span;

			int colon = lineSpan.IndexOf((byte)':');

			if (colon <= 0)
			{
				continue;
			}

			ReadOnlySpan<byte> name = lineSpan.Slice(0, colon).TrimEnd((byte)' ');
			ReadOnlyMemory<byte> value = line.Slice(colon + 1).Trim((byte)' ');

			if (Ascii.EqualsIgnoreCase(name, "Host"u8))
			{
				hostValue = value;
			}
			else if (!isConnect && Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
			{
				if (!TryAccumulateContentLength(value.Span, ref contentLength))
				{
					return false;
				}
			}
			else if (!isConnect && Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8))
			{
				isChunked = HasChunkedEncoding(value.Span);
			}
			else if (Ascii.EqualsIgnoreCase(name, "Proxy-Authorization"u8))
			{
				proxyAuth = value;
			}
		}

		// RFC 9112 §6.1: For requests, chunked must be the final transfer coding.
		// If TE is present but chunked is not last, body length cannot be determined.
		if (isChunked is false)
		{
			return false;
		}

		long? bodyLength = isChunked is true ? null : contentLength ?? 0;

		ReadOnlyMemory<byte> authority;
		ushort defaultPort = 80;

		if (isConnect)
		{
			// RFC 9110 §9.3.6: CONNECT request-target is the authority — always prefer it over Host.
			authority = uri;
			defaultPort = 443;
		}
		else
		{
			// RFC 9112 §3.2: origin-form starts with "/"; absolute-form starts with scheme.
			// Only extract authority from absolute-form URIs; origin-form uses Host header.
			ReadOnlySpan<byte> uriSpan = uri.Span;

			if (uriSpan.Length > 0 && uriSpan[0] is not (byte)'/' && uriSpan.IndexOf("://"u8) >= 0)
			{
				authority = ExtractAuthority(uri);
			}
			else if (!hostValue.IsEmpty)
			{
				authority = hostValue;
			}
			else
			{
				authority = uri;
			}

			// Determine default port from scheme
			if (uriSpan.Length > 8 && Ascii.EqualsIgnoreCase(uriSpan.Slice(0, 8), "https://"u8))
			{
				defaultPort = 443;
			}
		}

		if (!TryParseAuthority(authority, out ReadOnlyMemory<byte> host, out ushort port))
		{
			return false;
		}

		if (port is 0)
		{
			port = defaultPort;
		}

		result = new HttpHeaders(isConnect, host, port, bodyLength, proxyAuth);
		return true;
	}

	/// <summary>
	/// Extracts the authority portion from an absolute URI (e.g., "http://host:port/path" → "host:port").
	/// Caller must ensure the URI contains "://".
	/// </summary>
	private static ReadOnlyMemory<byte> ExtractAuthority(ReadOnlyMemory<byte> absoluteUri)
	{
		ReadOnlySpan<byte> uriSpan = absoluteUri.Span;
		int schemeEnd = uriSpan.IndexOf("://"u8);
		ReadOnlyMemory<byte> afterScheme = absoluteUri.Slice(schemeEnd + 3);

		// Authority ends at first '/' or '?' (RFC 3986: path-empty + query is valid)
		int end = afterScheme.Span.IndexOfAny((byte)'/', (byte)'?');

		return end < 0 ? afterScheme : afterScheme.Slice(0, end);
	}

	/// <summary>
	/// Parses an authority (host[:port]) from bytes. Handles IPv4, IPv6 ([::1]:port), and hostnames.
	/// Returns false if the authority is empty or structurally malformed (e.g. missing bracket, invalid port format).
	/// Does not validate the host content itself.
	/// </summary>
	private static bool TryParseAuthority(ReadOnlyMemory<byte> authority, out ReadOnlyMemory<byte> host, out ushort port)
	{
		port = 0;
		host = default;

		authority = authority.Trim((byte)' ');

		if (authority.IsEmpty)
		{
			return false;
		}

		ReadOnlySpan<byte> authoritySpan = authority.Span;

		// IPv6: [::1] or [::1]:port — trim brackets, extract optional port
		if (authoritySpan[0] is (byte)'[')
		{
			int close = authoritySpan.IndexOf((byte)']');

			if (close < 0)
			{
				return false;
			}

			host = authority.Slice(1, close - 1);
			ReadOnlySpan<byte> afterBracket = authoritySpan.Slice(close + 1);

			if (afterBracket.IsEmpty)
			{
				return !host.IsEmpty;
			}

			if (afterBracket.Length < 2 || afterBracket[0] is not (byte)':')
			{
				return false;
			}

			ReadOnlySpan<byte> portSpan = afterBracket.Slice(1);

			if (!Utf8Parser.TryParse(portSpan, out port, out int portConsumed) || portConsumed != portSpan.Length)
			{
				return false;
			}

			return !host.IsEmpty;
		}

		// host:port or host
		int colon = authoritySpan.LastIndexOf((byte)':');

		if (colon < 0)
		{
			host = authority;
		}
		else if (Utf8Parser.TryParse(authoritySpan.Slice(colon + 1), out port, out int consumed) && consumed == authoritySpan.Length - colon - 1)
		{
			host = authority.Slice(0, colon);
		}
		else
		{
			return false;
		}

		return !host.IsEmpty;
	}

	/// <summary>
	/// Returns true if the last comma-separated token in the Transfer-Encoding value is "chunked".
	/// Per RFC 9112 §6.1, chunked must be the final encoding.
	/// </summary>
	private static bool HasChunkedEncoding(ReadOnlySpan<byte> value)
	{
		int lastComma = value.LastIndexOf((byte)',');
		ReadOnlySpan<byte> lastToken = lastComma < 0 ? value : value.Slice(lastComma + 1);
		return Ascii.EqualsIgnoreCase(lastToken.Trim((byte)' '), "chunked"u8);
	}

	/// <summary>
	/// Accumulates a Content-Length header value, detecting invalid formats and conflicting values
	/// per RFC 9110 §8.6 and RFC 9112 §6.3.
	/// </summary>
	/// <returns>
	/// <see langword="true"/> if the value is valid and consistent with previous values;
	/// <see langword="false"/> if malformed or conflicting.
	/// </returns>
	private static bool TryAccumulateContentLength(ReadOnlySpan<byte> value, ref long? contentLength)
	{
		if (!Utf8Parser.TryParse(value, out long cl, out int consumed) || consumed != value.Length)
		{
			return false;
		}

		if (contentLength is not null && contentLength != cl)
		{
			return false;
		}

		contentLength = cl;
		return true;
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

	internal static bool TryFindHeaderEnd(ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> headerBytes, out long consumed)
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

	internal static bool TryParseChunkSize(ReadOnlySequence<byte> chunkSizeLine, out long chunkSize)
	{
		scoped ReadOnlySpan<byte> span;

		if (chunkSizeLine.IsSingleSegment)
		{
			span = chunkSizeLine.FirstSpan;
		}
		else
		{
			Span<byte> buf = stackalloc byte[(int)chunkSizeLine.Length];
			chunkSizeLine.CopyTo(buf);
			span = buf;
		}

		int semi = span.IndexOf((byte)';');
		ReadOnlySpan<byte> hex = semi >= 0 ? span.Slice(0, semi) : span;

		if (!Utf8Parser.TryParse(hex, out chunkSize, out int consumed, 'X') || consumed != hex.Length)
		{
			return false;
		}

		return true;
	}
}
