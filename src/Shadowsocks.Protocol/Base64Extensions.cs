using System.Buffers;
using System.Text;

namespace Shadowsocks.Protocol;

internal static class Base64Extensions
{
	private static Encoding Encoding => Encoding.UTF8;

	public static string ToBase64UrlSafe(this string raw)
	{
		return raw.AsSpan().ToBase64UrlSafe();
	}

	public static string ToBase64UrlSafe(this ReadOnlySpan<char> raw)
	{
		byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.GetMaxByteCount(raw.Length));
		try
		{
			int length = Encoding.GetBytes(raw, buffer);
			return Convert.ToBase64String(buffer.AsSpan(0, length)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	public static string FromBase64UrlSafe(this string raw)
	{
		byte[] buffer = Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/').PadRight(raw.Length + (4 - raw.Length % 4) % 4, '='));
		return Encoding.GetString(buffer);
	}
}
