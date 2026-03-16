namespace Shadowsocks.Crypto;

internal static class SpanExtensions
{
	/// <summary>
	/// Increment a little-endian byte span as a counter
	/// </summary>
	public static void Increment(this Span<byte> span)
	{
		for (int i = 0; i < span.Length; i++)
		{
			if (++span[i] != 0)
			{
				break;
			}
		}
	}
}
