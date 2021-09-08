using HttpProxy.Enums;
using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Text;

namespace HttpProxy
{
	public static class HttpUtils
	{
		public static ReadOnlySpan<byte> HttpHeaderEnd => new[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
		public static ReadOnlySpan<byte> HttpNewLineSpan => new[] { (byte)'\r', (byte)'\n' };
		private static readonly char[] NewLines = { '\r', '\n' };
		public const string HttpNewLine = "\r\n";

		// https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers
		private static readonly ImmutableHashSet<string> HopByHopHeaders =
			ImmutableHashSet.Create(
				StringComparer.OrdinalIgnoreCase,
				@"CONNECTION",
				@"KEEP-ALIVE",
				@"PROXY-AUTHENTICATE",
				@"PROXY-AUTHORIZATION",
				@"TE",
				@"TRAILER",
				@"TRANSFER-ENCODING",
				@"UPGRADE",
				@"Proxy-Connection"
			);

		internal static string BuildErrorResponse(ConnectionErrorResult error, string httpVersion)
		{
			return error switch
			{
				ConnectionErrorResult.AuthenticationError => $"{httpVersion} 401 Unauthorized\r\n\r\n",
				ConnectionErrorResult.HostUnreachable     => $"{httpVersion} 502 {error}\r\n\r\n",
				ConnectionErrorResult.ConnectionRefused   => $"{httpVersion} 502 {error}\r\n\r\n",
				ConnectionErrorResult.ConnectionReset     => $"{httpVersion} 502 {error}\r\n\r\n",
				_                                         => $"{httpVersion} 500 Internal Server Error\r\nX-Proxy-Error-Type: {error}\r\n\r\n"
			};
		}

		internal static bool IsHopByHopHeader(this string header)
		{
			return HopByHopHeaders.Contains(header);
		}

		internal static string[] SplitLines(this string str)
		{
			return str.Split(NewLines, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		}

		public static bool IsHttpHeader(ReadOnlySequence<byte> buffer)
		{
			var reader = new SequenceReader<byte>(buffer);
			if (!reader.TryReadTo(out ReadOnlySequence<byte> headerBuffer, HttpHeaderEnd))
			{
				return false;
			}

			var headers = Encoding.UTF8.GetString(headerBuffer); // 不包括结尾的 \r\n\r\n

			var headerLines = headers.SplitLines();

			if (headerLines.Length <= 0)
			{
				return false;
			}

			var methodLine = headerLines[0].Split(' ');
			if (methodLine.Length is not 3) // METHOD URI HTTP/X.Y
			{
				return false;
			}

			return true;
		}
	}
}
