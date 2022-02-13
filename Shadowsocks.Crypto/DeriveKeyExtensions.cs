using CryptoBase.Abstractions.Digests;
using CryptoBase.Digests;
using System.Buffers;
using System.Text;

namespace Shadowsocks.Crypto;

public static class DeriveKeyExtensions
{
	public static void SsDeriveKey(this Span<byte> key, string password)
	{
		const int hashLength = HashConstants.Md5Length;
		int pwMaxSize = Encoding.UTF8.GetMaxByteCount(password.Length);

		byte[] buffer = ArrayPool<byte>.Shared.Rent(pwMaxSize + pwMaxSize + hashLength);
		try
		{
			int pwLength = Encoding.UTF8.GetBytes(password, buffer);
			Span<byte> pw = buffer.AsSpan(0, pwLength);
			Span<byte> result = buffer.AsSpan(pwLength, hashLength + pwLength);
			Span<byte> low = result[hashLength..];

			pw.ToMd5(result);
			result[..Math.Min(hashLength, key.Length)].CopyTo(key);

			for (int i = hashLength; i < key.Length; i += hashLength)
			{
				pw.CopyTo(low);
				result.ToMd5(result);

				int length = Math.Min(hashLength, key.Length - i);
				result[..length].CopyTo(key[i..]);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	public static void ToMd5(this in Span<byte> origin, Span<byte> destination)
	{
		ToMd5((ReadOnlySpan<byte>)origin, destination);
	}

	public static void ToMd5(this in ReadOnlySpan<byte> origin, Span<byte> destination)
	{
		using IHash hasher = DigestUtils.Create(DigestType.Md5);
		hasher.UpdateFinal(origin, destination);
	}
}
