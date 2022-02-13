using Shadowsocks.Crypto;
using Shadowsocks.Crypto.AEAD;
using System.Diagnostics;

namespace Shadowsocks.SpeedTest;

public static class CryptoTest
{
	private const string Password = @"密码";

	private const int Step = 1 * 1024 * 1024;

	public static void Test(string method)
	{
		Console.Write($@"Testing {method}: ");

		ReadOnlySpan<byte> i = new byte[Step];
		Span<byte> o = new byte[AEADShadowsocksCrypto.GetBufferSize(i.Length)];

		using IShadowsocksCrypto crypto = ShadowsocksCrypto.Create(method, Password);
		ulong length = 0ul;
		Stopwatch sw = Stopwatch.StartNew();

		do
		{
			crypto.EncryptTCP(i, o, out int pLength, out _);
			length += (uint)pLength;
		} while (sw.ElapsedMilliseconds < 2000);

		sw.Stop();
		double result = length / sw.Elapsed.TotalSeconds / 1024.0 / 1024.0;
		Console.WriteLine($@"{result:F2} MB/s");
	}
}
