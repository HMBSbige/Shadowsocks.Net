using CryptoBase.Abstractions.Digests;
using CryptoBase.Digests.MD5;
using System;
using System.Buffers;
using System.Text;

namespace Shadowsocks.Crypto
{
	public static class DeriveKeyExtensions
	{
		public static void SsDeriveKey(this Span<byte> key, string password)
		{
			const int hashLength = HashConstants.Md5Length;
			var pwMaxSize = Encoding.UTF8.GetMaxByteCount(password.Length);

			var buffer = ArrayPool<byte>.Shared.Rent(pwMaxSize + pwMaxSize + hashLength);
			try
			{
				var pwLength = Encoding.UTF8.GetBytes(password, buffer);
				var pw = buffer.AsSpan(0, pwLength);
				var result = buffer.AsSpan(pwLength, hashLength + pwLength);
				var low = result.Slice(hashLength);

				MD5Utils.Default(pw, result);
				result.Slice(0, Math.Min(hashLength, key.Length)).CopyTo(key);

				for (var i = hashLength; i < key.Length; i += hashLength)
				{
					pw.CopyTo(low);
					MD5Utils.Default(result, result);

					var length = Math.Min(hashLength, key.Length - i);
					result.Slice(0, length).CopyTo(key.Slice(i));
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}
	}
}
