using HttpProxy.Enums;
using System;
using System.Linq;

namespace HttpProxy
{
	internal static class Utils
	{
		public static string BuildErrorResponse(ConnectionErrorResult error, string httpVersion)
		{
			return error switch
			{
				ConnectionErrorResult.AuthenticationError => $"{httpVersion} 401 Unauthorized\r\n\r\n",
				ConnectionErrorResult.HostUnreachable => $"{httpVersion} 502 {error}\r\n\r\n",
				ConnectionErrorResult.ConnectionRefused => $"{httpVersion} 502 {error}\r\n\r\n",
				ConnectionErrorResult.ConnectionReset => $"{httpVersion} 502 {error}\r\n\r\n",
				_ => $"{httpVersion} 500 Internal Server Error\r\nX-Proxy-Error-Type: {error}\r\n\r\n"
			};
		}

		// https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers
		private static readonly string[] HopByHopHeaders =
		{
			@"CONNECTION",
			@"KEEP-ALIVE",
			@"PROXY-AUTHENTICATE",
			@"PROXY-AUTHORIZATION",
			@"TE",
			@"TRAILER",
			@"TRANSFER-ENCODING",
			@"UPGRADE"
		};

		public static bool IsHopByHopHeader(this string header)
		{
			return HopByHopHeaders.Contains(header, StringComparer.OrdinalIgnoreCase);
		}
	}
}
