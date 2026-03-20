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
}
