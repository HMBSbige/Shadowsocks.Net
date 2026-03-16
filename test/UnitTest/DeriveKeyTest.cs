using Shadowsocks.Crypto;

namespace UnitTest;

public class DeriveKeyTest
{
	[Test]
	[Arguments(@"123中1文da18测)试abc!#$@(", @"8c5ea5e7ade3b3133a479e6aff0506dd7952bf75984a26722070cd9e44a09353")]
	[Arguments(@"Imakethis_LongPassPhraseFor_safety_2019_0928@_@!", @"4b01a2d762fada9ede4d1034a13dc69c3b528b738236b99cd3a472d2933580d6")]
	public async Task SsDeriveKey(string password, string keyHexStr)
	{
		for (int i = 16; i <= 32; ++i)
		{
			byte[] key = new byte[i];
			((Span<byte>)key).SsDeriveKey(password);

			await Assert.That(Convert.ToHexStringLower(key)).IsEqualTo(keyHexStr[..(i << 1)]);
		}
	}
}
