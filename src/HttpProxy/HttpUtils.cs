using System.Buffers;

namespace HttpProxy;

/// <summary>
/// Low-level HTTP header parsing and rewriting utilities operating directly on byte spans.
/// </summary>
public static partial class HttpUtils
{
	/// <summary>
	/// The byte sequence <c>\r\n\r\n</c> that terminates an HTTP header block.
	/// </summary>
	public static ReadOnlySpan<byte> HttpHeaderEnd => "\r\n\r\n"u8;

	/// <summary>
	/// The byte sequence <c>\r\n</c> used as a line separator in HTTP headers.
	/// </summary>
	public static ReadOnlySpan<byte> HttpNewLine => "\r\n"u8;

	/// <summary>
	/// Checks whether the buffer starts with a valid HTTP request header.
	/// </summary>
	public static bool IsHttpHeader(this ReadOnlySequence<byte> buffer)
	{
		SequenceReader<byte> reader = new(buffer);

		if (!reader.TryReadTo(out ReadOnlySequence<byte> headerBuffer, HttpHeaderEnd))
		{
			return false;
		}

		// Get request line (up to first \r\n, or entire headerBuffer if no additional headers)
		reader = new SequenceReader<byte>(headerBuffer);

		if (!reader.TryReadTo(out ReadOnlySequence<byte> requestLine, HttpNewLine))
		{
			requestLine = headerBuffer;
		}

		// METHOD request-target HTTP-version
		reader = new SequenceReader<byte>(requestLine);

		if (!reader.TryReadTo(out ReadOnlySequence<byte> _, (byte)' ') || !reader.TryReadTo(out ReadOnlySequence<byte> _, (byte)' '))
		{
			return false;
		}

		// Version must start with "HTTP/"
		return reader.IsNext("HTTP/"u8);
	}
}
