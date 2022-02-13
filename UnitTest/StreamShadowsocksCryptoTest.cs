using CryptoBase.DataFormatExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Crypto;
using Shadowsocks.Crypto.Stream;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace UnitTest;

[TestClass]
public class StreamShadowsocksCryptoTest
{
	private const string Password = @"密码";
	private const string Origin = @"Glib jocks quiz nymph to vex dwarf.1145141919810!这是原文！";
	private const string Origin2 = @"这是原文";

	private static void IsSymmetrical(string method, string password)
	{
		IsSymmetricalTcp(method, password);
		IsSymmetricalUdp(method, password);
	}

	private static void IsSymmetricalTcp(string method, string password)
	{
		Span<byte> buffer = new byte[8192];
		RandomNumberGenerator.Fill(buffer);
		string originHex = buffer.ToHex();

		using StreamShadowsocksCrypto crypto = (StreamShadowsocksCrypto)ShadowsocksCrypto.Create(method, password);
		int length = crypto.IvLength + buffer.Length;
		Span<byte> output = new byte[length];

		crypto.EncryptTCP(buffer, output, out int processLength, out int outLength);
		Assert.AreEqual(buffer.Length, processLength);
		Assert.AreEqual(length, outLength);

		Span<byte> encBuffer = output[..outLength];

		crypto.Reset();

		ReadOnlySequence<byte> sequence = new(encBuffer.ToArray());

		int decLength = crypto.DecryptTCP(ref sequence, buffer);
		Assert.AreEqual(0, sequence.Length);
		Assert.AreEqual(buffer.Length, decLength);

		Assert.AreEqual(originHex, buffer.ToHex());
	}

	private static void IsSymmetricalUdp(string method, string password)
	{
		Span<byte> buffer = new byte[8192];
		RandomNumberGenerator.Fill(buffer);
		string originHex = buffer.ToHex();

		using StreamShadowsocksCrypto crypto = (StreamShadowsocksCrypto)ShadowsocksCrypto.Create(method, password);
		int length = crypto.IvLength + buffer.Length;
		Span<byte> output = new byte[length];

		int outLength = crypto.EncryptUDP(buffer, output);
		Assert.AreEqual(length, outLength);

		Span<byte> encBuffer = output[..outLength];

		outLength = crypto.DecryptUDP(encBuffer, buffer);
		Assert.AreEqual(buffer.Length, outLength);

		Assert.AreEqual(originHex, buffer.ToHex());
	}

	private static void TestDecrypt(StreamShadowsocksCrypto crypto, string str, string encHex)
	{
		Span<byte> origin = Encoding.UTF8.GetBytes(str);
		ReadOnlySequence<byte> enc = new(encHex.FromHex());

		Span<byte> buffer = new byte[origin.Length];

		int e0Length = Math.Max(0, crypto.IvLength - 1);
		ReadOnlySequence<byte> e0 = enc.Slice(0, e0Length);
		int length0 = crypto.DecryptTCP(ref e0, buffer);
		Assert.AreEqual(e0Length, e0.Length);
		Assert.AreEqual(0, length0);

		int outLength = crypto.DecryptTCP(ref enc, buffer);
		Assert.AreEqual(0, enc.Length);
		Assert.AreEqual(origin.Length, outLength);

		Assert.AreEqual(str, Encoding.UTF8.GetString(buffer));
	}

	[TestMethod]
	[DataRow(@"sm4-cfb")]
	public void WrongMethod(string method)
	{
		Assert.ThrowsException<ArgumentException>(
			() =>
			{
				using IShadowsocksCrypto crypto = ShadowsocksCrypto.Create(method, string.Empty);
			}
		);
	}

	[TestMethod]
	public void SetIv()
	{
		Assert.ThrowsException<ArgumentException>(
			() =>
			{
				using ChaCha20IETFShadowsocksCrypto crypto = new(Password);
				Span<byte> iv = new byte[crypto.IvLength];
				RandomNumberGenerator.Fill(iv);

				crypto.SetIv(iv);
				Assert.IsTrue(crypto.Iv.AsSpan(0, crypto.IvLength).SequenceEqual(iv));

				crypto.SetIv(Array.Empty<byte>());
			}
		);
	}

	[TestMethod]
	[DataRow(Origin, @"476c6962206a6f636b73207175697a206e796d706820746f207665782064776172662e3131343531343139313938313021e8bf99e698afe58e9fe69687efbc81")]
	[DataRow(Origin2, @"e8bf99e698afe58e9fe69687")]
	public void None(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.NoneMethod, Password);
		IsSymmetrical(ShadowsocksCrypto.PlainMethod, Password);

		using NoneShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(16, crypto.KeyLength);
		Assert.AreEqual(0, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"c572b607c68b49de991ed72a3eb88bc83ded59bcab833802a32e0f9904cb3ed8521856fe31e89769ad2694fe6d8e66fc7f2f6214b2efada73222240e6c75c291")]
	[DataRow(Origin2, @"6aa146837e4ec3336d8b61dc")]
	public void Rc4(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.Rc4Method, Password);

		using Rc4ShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(16, crypto.KeyLength);
		Assert.AreEqual(0, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"8c3f58d77b99095ef7a8b387dda6d5b11100eb50dfd722d9275bcd8a04ba96514a56a348757e69c793a88db28585c576d68e825e0b8871288e1705d2d8a2087bc155c468a3f17bdcfaa94a34bce268d1")]
	[DataRow(Origin2, @"6f1c36c5152ca8391e4360aa89085f3f100ad16e0ac5e94ca7ac084e")]
	public void Rc4Md5(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.Rc4Md5Method, Password);

		using Rc4Md5ShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(16, crypto.KeyLength);
		Assert.AreEqual(16, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"71d861bbc6f1771b7b0a4d40c64b7ebe8a29769d5d97fc30200169404eb0d7bcca257f71d4420d04b49e702c1b6d7ad6c74d897e015c9a4bb632e5786bcf4a810797d80dc00a")]
	[DataRow(Origin2, @"73bce8c1d3be621f2e1643e0d7c6bab34740")]
	public void Rc4Md56(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.Rc4Md56Method, Password);

		using Rc4Md56ShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(16, crypto.KeyLength);
		Assert.AreEqual(6, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"52e6a5bc98dec2f9d41b7845fdff6c25592f31361d0e6048a5e4e57da028ff7c50e0bfd5272ad8c76ba5b6bc1c5233ee4105319b04822c588a1313e311e47ed84d5a47991fa96f432a3a7052d9d4792c")]
	[DataRow(Origin2, @"9e939c64f9c2722ce897ceea294b49b275cd0b7c26e61a86c0f8f3c5")]
	public void Aes128Ctr(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.Aes128CtrMethod, Password);

		using Aes128CtrShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(16, crypto.KeyLength);
		Assert.AreEqual(16, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"444f8c66ec9a5f88825fffea9afa79e7e621ad7ca6e61bf202014de07cb542e80436aa3882763bb7e94856a74f378dcfdd03caae1f1b51484f127a0c6218b1c6cb56ccbeb21555c06c521439b46788b6")]
	[DataRow(Origin2, @"4bb15994e15e8194b6665c1597bcbdc2c31c596d8dc8c33cc3cd0690")]
	public void Aes192Ctr(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.Aes192CtrMethod, Password);

		using Aes192CtrShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(24, crypto.KeyLength);
		Assert.AreEqual(16, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"8751054bf37abfb0bd3b30df879c8249b1f49cdc1e75d29c6c18ff6f7c118e6794bd4acdfdce5f9ab1eefcc7739a3d5cf0f8b2db4ba5c1dfd108b117ff723c3c45f0c3c1f73c08bc0f7d7064f227ca03")]
	[DataRow(Origin2, @"519603d859e9da011e5d0a5a91ca38751744171ead9c0cc7a35c46aa")]
	public void Aes256Ctr(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.Aes256CtrMethod, Password);

		using Aes256CtrShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(32, crypto.KeyLength);
		Assert.AreEqual(16, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"dc4e9eb1519bfeaafcb97afbf2f1a98241f9a89099caf93e2259602a6d9de79db4ae556923476c01de6d149d11214b22a64027da7131f013572b38e005d7249d826c92b422a90365651666ddf226c26d")]
	[DataRow(Origin2, @"6a9da19cda7a9d60a1ea7b092af62059d8eaf6b3a94c43dd47d5fa6d")]
	public void Aes128Cfb(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.Aes128CfbMethod, Password);

		using Aes128CfbShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(16, crypto.KeyLength);
		Assert.AreEqual(16, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"ba94d5ce35d5d96cceba37765244c6dc82306ca0d74393dc2f6c5db092363f796c27d14f89329532aec40897fef49eb693f60de6128b515968a34744aa72836ff5bac78266d779584d7a40ba78a27e00")]
	[DataRow(Origin2, @"d7104b5592ecc8068fcfc7663cc1796f08ff6150de511e79f4326cb9")]
	public void Aes192Cfb(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.Aes192CfbMethod, Password);

		using Aes192CfbShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(24, crypto.KeyLength);
		Assert.AreEqual(16, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"5f0d1d4407576b4f239387ce861b81c3808108a20d898ad75d78abf8534892f83e81662ea86e47f0d5f78fa6afadd8c77b2c9c27c7db509ac6d76ec819c9332cbd9cd89690abd3a39d8658bd97b63ef5")]
	[DataRow(Origin2, @"8ad24b29c2b0ed9eda1393224ec816f8c2cefebf757c0cc68e61a6ec")]
	public void Aes256Cfb(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.Aes256CfbMethod, Password);

		using Aes256CfbShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(32, crypto.KeyLength);
		Assert.AreEqual(16, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"57902c3c352bc6af8483bcf6d75c2cb0e134eaa1a0b1e487fb61530d0f372fbad08c69625509bf7722c24e64c49d912f95aeb49356919f6d3e5e1c646f801545f876250b642a93365d93f28b")]
	[DataRow(Origin2, @"da5987eec8817053b20ed413c78da5bfad2a0991163bf476")]
	public void ChaCha20IETF(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.ChaCha20IetfMethod, Password);

		using ChaCha20IETFShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(32, crypto.KeyLength);
		Assert.AreEqual(12, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"93ac920ee68f96ce7afbcda881993feb1b297b788c73faa9f2e1a94ddde3f51e2eddcb9c08f0e63e51c1498f233edd9b44d230dffdabc5d763c1a84c172aa132ab1fc67f26992670")]
	[DataRow(Origin2, @"7bef09b2b9bd26d9f0a449bb3619085178e274f6")]
	public void ChaCha20(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.ChaCha20Method, Password);

		using ChaCha20ShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(32, crypto.KeyLength);
		Assert.AreEqual(8, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"0b22c7cb3e3338eb22867db629fc22d0ce613acd28edd0c314c3b0f869d1e2d1b61eb5b208e60671544d8ad1afb29cb77459bce98424e346ba71607ae38ab46c4632e71ad7090f33")]
	[DataRow(Origin2, @"35401af3185322b4c69e34909aa57b6963bb897e")]
	public void Salsa20(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.Salsa20Method, Password);

		using Salsa20ShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(32, crypto.KeyLength);
		Assert.AreEqual(8, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"0f128d4109284ef05a93ecbb501c92179ecb9ab7d9360e9f51c90e4cd4b1cc0a3e7ba7074a967b2f620b9c606becc4c47fdb9570a603ee4d05744a0897e40ae445942fac1aa1ab755cc285630fb561166d7c487450f43d37")]
	[DataRow(Origin2, @"5331bed48123fdc5016d6de107e3121f77a11a4bdc014b5902cf7e4b0c7473d50d3dd580")]
	public void XSalsa20(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.XSalsa20Method, Password);

		using XSalsa20ShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(32, crypto.KeyLength);
		Assert.AreEqual(24, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"e7c687ba317b18e1a4eccc3da6c986c1eb47d6185553d4c6c1b8b8cf73fea858231e9db70e9665751d2a3b1b015803b896ea7fdb4712e0a157e0540d4a0fca013c50f7ead87034a855f32aa363bfa6b82c5aa88b4c164aa8")]
	[DataRow(Origin2, @"e4edff00057732d03bd757ce6f2c776c68aa312eb8e8a889e277fe3994d4b1ee83618c55")]
	public void XChaCha20(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.XChaCha20Method, Password);

		using XChaCha20ShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(32, crypto.KeyLength);
		Assert.AreEqual(24, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}

	[TestMethod]
	[DataRow(Origin, @"7a692e2f97abcc219c3e5572428269e1e26fd1d89bd7b7db1ae623c25831557e36229a1b431fc21446a359feaed618ea262ad6da477028aeec11a1be88676dac62849861375e4ae5faa8857bfd43562a")]
	[DataRow(Origin2, @"f88a929c0cfb2d09dcbfc3944e4b26539aeb2e065b7b8b079e378e22")]
	public void Sm4Ctr(string str, string encHex)
	{
		IsSymmetrical(ShadowsocksCrypto.Sm4CtrMethod, Password);

		using Sm4CtrShadowsocksCrypto crypto = new(Password);
		Assert.AreEqual(16, crypto.KeyLength);
		Assert.AreEqual(16, crypto.IvLength);

		TestDecrypt(crypto, str, encHex);
	}
}
