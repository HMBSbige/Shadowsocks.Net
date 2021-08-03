using CryptoBase.DataFormatExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Crypto;
using System;

namespace UnitTest
{
	[TestClass]
	public class DeriveKeyTest
	{
		[TestMethod]
		[DataRow(@"123中1文da18测)试abc!#$@(", @"8c5ea5e7ade3b3133a479e6aff0506dd7952bf75984a26722070cd9e44a09353")]
		[DataRow(@"Imakethis_LongPassPhraseFor_safety_2019_0928@_@!", @"4b01a2d762fada9ede4d1034a13dc69c3b528b738236b99cd3a472d2933580d6")]
		public void SsDeriveKey(string password, string keyHexStr)
		{
			for (var i = 16; i <= 32; ++i)
			{
				Span<byte> key = new byte[i];
				key.SsDeriveKey(password);

				Assert.AreEqual(keyHexStr[..(i << 1)], key.ToHex());
			}
		}
	}
}
