using CryptoBase;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Crypto;
using Shadowsocks.Crypto.AEAD;
using System;
using System.Security.Cryptography;
using System.Text;

namespace UnitTest
{
	[TestClass]
	public class AEADShadowsocksCryptoTest
	{
		private const string Password = @"密码";
		private const string Origin = @"Glib jocks quiz nymph to vex dwarf.1145141919810!这是原文！";
		private const string Origin2 = @"这是原文";

		private static void TestAEAD(string method, string password, string str, string encHex)
		{
			TestTcpDecrypt(method, password, str, encHex);
			IsSymmetricalTcp(method, password, AEADShadowsocksCrypto.ReceiveSize);
			IsSymmetricalTcp(method, password, AEADShadowsocksCrypto.BufferSize);
			IsSymmetricalUdp(method, password, AEADShadowsocksCrypto.ReceiveSize);
			TestException(method, password);
		}

		private static void TestTcpDecrypt(string method, string password, string str, string encHex)
		{
			Span<byte> origin = Encoding.UTF8.GetBytes(str);
			Span<byte> enc = encHex.FromHex();

			Span<byte> buffer = new byte[enc.Length];

			using var crypto = (AEADShadowsocksCrypto)ShadowsocksCrypto.Create(method, password);

			crypto.DecryptTCP(enc.Slice(0, crypto.SaltLength - 1), buffer, out var p2, out var o2);
			Assert.AreEqual(0, p2);
			Assert.AreEqual(0, o2);

			crypto.DecryptTCP(enc.Slice(0, crypto.SaltLength + 1), buffer, out var p0, out var o0);
			Assert.AreEqual(crypto.SaltLength, p0);
			Assert.AreEqual(0, o0);

			var remain = enc.Slice(p0);
			crypto.DecryptTCP(remain, buffer.Slice(o0), out var p1, out var o1);
			Assert.AreEqual(remain.Length, p1);
			Assert.AreEqual(origin.Length, o1);

			Assert.AreEqual(str, Encoding.UTF8.GetString(buffer.Slice(0, o1)));
		}

		private static void IsSymmetricalTcp(string method, string password, int size)
		{
			Span<byte> buffer = new byte[size];
			RandomNumberGenerator.Fill(buffer);
			var originHex = buffer.ToHex();

			Span<byte> output = new byte[AEADShadowsocksCrypto.BufferSize + size];

			using var encryptor = (AEADShadowsocksCrypto)ShadowsocksCrypto.Create(method, password);
			using var decryptor = (AEADShadowsocksCrypto)ShadowsocksCrypto.Create(method, password);

			encryptor.AddressBufferLength = decryptor.AddressBufferLength = 7;

			encryptor.EncryptTCP(buffer.Slice(0, 1), output, out var p2, out var o2);
			Assert.AreEqual(0, p2);
			Assert.AreEqual(0, o2);

			encryptor.EncryptTCP(buffer, output, out var p0, out var o0);
			Assert.AreNotEqual(0, p0);
			Assert.AreNotEqual(0, o0);

			var encBuffer = output.Slice(0, o0);

			decryptor.DecryptTCP(encBuffer, buffer, out var p1, out var o1);
			Assert.AreEqual(encBuffer.Length, p1);
			Assert.AreEqual(buffer.Length, o1);

			Assert.AreEqual(originHex, buffer.ToHex());
		}

		private static void IsSymmetricalUdp(string method, string password, int size)
		{
			Span<byte> buffer = new byte[size];
			RandomNumberGenerator.Fill(buffer);
			var originHex = buffer.ToHex();

			Span<byte> output = new byte[AEADShadowsocksCrypto.BufferSize];

			using var encryptor = (AEADShadowsocksCrypto)ShadowsocksCrypto.Create(method, password);
			using var decryptor = (AEADShadowsocksCrypto)ShadowsocksCrypto.Create(method, password);

			var o0 = encryptor.EncryptUDP(buffer, output);
			Assert.AreNotEqual(0, o0);

			var encBuffer = output.Slice(0, o0);

			var o1 = decryptor.DecryptUDP(encBuffer, buffer);
			Assert.AreEqual(buffer.Length, o1);

			Assert.AreEqual(originHex, buffer.ToHex());
		}

		private static void TestException(string method, string password)
		{
			// AddressBufferLength too large
			Assert.ThrowsException<Exception>(() =>
			{
				Span<byte> buffer = new byte[ushort.MaxValue];
				Span<byte> output = new byte[AEADShadowsocksCrypto.BufferSize];
				using var crypto = (AEADShadowsocksCrypto)ShadowsocksCrypto.Create(method, password);
				crypto.AddressBufferLength = AEADShadowsocksCrypto.PayloadLengthLimit + 1;
				crypto.EncryptTCP(buffer, output, out _, out _);
			});

			// Received part of stream
			{
				Span<byte> buffer = new byte[114];
				Span<byte> output = new byte[AEADShadowsocksCrypto.BufferSize];
				using var encryptor = (AEADShadowsocksCrypto)ShadowsocksCrypto.Create(method, password);
				using var decryptor = (AEADShadowsocksCrypto)ShadowsocksCrypto.Create(method, password);
				encryptor.AddressBufferLength = 7;
				encryptor.EncryptTCP(buffer, output, out var p0, out var o0);
				Assert.AreEqual(buffer.Length, p0);

				var part = output.Slice(0, o0 - 1);
				decryptor.DecryptTCP(part, buffer, out var p1, out _);
				Assert.IsTrue(p1 < part.Length);
			}
		}

		[TestMethod]
		public void CheckInfoBytes()
		{
			Assert.AreEqual(@"ss-subkey", Encoding.UTF8.GetString(ShadowsocksCrypto.InfoBytes));
		}

		[TestMethod]
		[DataRow(Origin, @"9b0006e19ff614e5eb44a5c9190268d243b5356463c8760d52db2dcb4ca3cd00747f4237ba8e94d3e498e0d8101d7610752766785190b9a869d3aee3db4db5411eea5a6b0e16d1bbd469bfab72a8cc6745a70b71472ec76aa189363cf5d71d98f46a6dbde1acca5de7083c8e27dc386daa6c460540eea8f0d223e8d890d34f45cd5b912b7fcfc3cc20156d1cbbd3066e5cadce99")]
		[DataRow(Origin2, @"c3b3e419f8de8c2d4701d94f5258915a967bf7b27a9e97e4cee0c00d67caa7c992f4f41d83109f0a9a6710871501b64851ffa1588c0603c168e00a4ac50b4d729203aecfddd1475b78e338bfe943662fe25f2fe49afc1f5eb00389113d417e5e")]
		public void Aes128Gcm(string str, string encHex)
		{
			TestAEAD(ShadowsocksCrypto.Aes128GcmMethod, Password, str, encHex);

			// Received wrong payload length
			Assert.ThrowsException<Exception>(() =>
			{
				Span<byte> buffer = new byte[AEADShadowsocksCrypto.BufferSize];
				Span<byte> data = @"9fe197e9b49097273bd814f2955f2056527fcedc730b1c28b37f37520900ad518fb91bd61e4b8226cdb5a69109c713e2c37db1ffb980b09aafc3a56857df3eec3cb3b10edd9aa8e257851cff2250d4cbc952cf9c5103e8311c4e9b18fa68e0734716294747c1abcc906be8a0ce33d41438795cd682cdf7b4e1949b398656543ca03ed7af3cfbcb6700646c241baa3bb39a3f175f5d127d27649876cb055d8b271f48a5abeead43ad5f9d9ac2d47b12997fa8d45f4c370dd49b683c772b30ed092f6326980c5c".FromHex();
				using var crypto = new Aes128GcmShadowsocksCrypto(Password);
				Assert.AreEqual(16, crypto.KeyLength);
				Assert.AreEqual(16, crypto.SaltLength);
				Assert.AreEqual(12, crypto.NonceLength);

				crypto.DecryptTCP(data, buffer, out _, out _);
			});
		}

		[TestMethod]
		[DataRow(Origin, @"f3cebc6fb635c16cf1c5d7dfc1350c9c5fd1c51bdd93c68b49be9a856c9230db07e8619cea8c05cf66417fd1962838b822e455f915b594393a6486041c740e41470986d32e091461d5fe2739888195db181909e13fe867764fca8730a437709a577f486cccabdc2f3918d4d0fa3fb96fddc35d8ae6d18073c54570aed05291c558d2817b3a1ef23af4be14a36c297d034844f1780de3b6f7c143ac9a")]
		[DataRow(Origin2, @"c58266923546894ea26c786e1cb6e35a4a15ac9b5ba83cbdf43187f7b1d53bd0bb4e61821d519c71eb2fdcea0573aaaa0a7a0ba59a3e4e966b51750b2602c740f293720a31665f879332974ad715b0cd7a8e1428d9b7c4b71f5ed07c539f7f1a296dd39ffe354924")]
		public void Aes192Gcm(string str, string encHex)
		{
			TestAEAD(ShadowsocksCrypto.Aes192GcmMethod, Password, str, encHex);

			using var crypto = new Aes192GcmShadowsocksCrypto(Password);
			Assert.AreEqual(24, crypto.KeyLength);
			Assert.AreEqual(24, crypto.SaltLength);
			Assert.AreEqual(12, crypto.NonceLength);
		}

		[TestMethod]
		[DataRow(Origin, @"4fb44de9876054b81d156438024560fc0ef0118e294eb0950490acc0f2083595ba06df583bafcb96e7ac2916e7d064cc1f8e687aba688cb29e500cef11cb91223c17d62e0f8b7bc15d2ff7a18658f30e4e1b66a80cb3f70cfa110fceaa2c50a2271d533f3c15ba427a9ebf96cc7f0426d79c85e6dfbd3c7b26cd2a663629125feafe65659942d0fe29945e3a80b6a7e253bb400c7f0f0fcad0eb3e455d66d178af51f51a")]
		[DataRow(Origin2, @"5648a2d3e86fe68d7c137ad894294f8e8b57ce55eb545cb7da69b93708e2a689a4a8798171d001ed9ef8d3e1b1c89d862604ce7e0803584719ea65e0c4025800187a0e0a2c5408519e93b2279358fb81842e9c913a76ee93ee25d4ddbfb4729fcc64411054411a01a4ada4f22c5e401e")]
		public void Aes256Gcm(string str, string encHex)
		{
			TestAEAD(ShadowsocksCrypto.Aes256GcmMethod, Password, str, encHex);

			using var crypto = new Aes256GcmShadowsocksCrypto(Password);
			Assert.AreEqual(32, crypto.KeyLength);
			Assert.AreEqual(32, crypto.SaltLength);
			Assert.AreEqual(12, crypto.NonceLength);
		}

		[TestMethod]
		[DataRow(Origin, @"8094ac0782801abeb1e60778767df9b4310cacf43d6e52f19371e19fcc7716dd5ccf2b739de27db535ab0571ebeb2b30a3652ffedfaa77d47d9a97f21b015d056902d4aa198cc2e5588a8dddd36a8abd9dd3154c3837e86cf36f6471eead1166ed485cd4da93fd7caa27be5682788cf61e36371ab700803e18633b1011dd08df6553df8b7e5adc7bfc6c2f58b531039b044437f74f86a739dc70410f5db04b387988cc8e")]
		[DataRow(Origin2, @"8bd996a259f861d54c4b27e740a6ad23381631dae3ab69e5c0377625752fa0f5dc68c4ae6a4c6b2c4f6bdda92ffad4e11fbf5b63bf05809d419cd0b562e927b370c627f14f42b18e146c832bfb6e6bd1f58cc71333ac60943d5592a8f8148687d6008defd21c791fc199efe26dc136e5")]
		public void ChaCha20Poly1305(string str, string encHex)
		{
			TestAEAD(ShadowsocksCrypto.ChaCha20IetfPoly1305Method, Password, str, encHex);

			using var crypto = new ChaChaPoly1305ShadowsocksCrypto(Password);
			Assert.AreEqual(32, crypto.KeyLength);
			Assert.AreEqual(32, crypto.SaltLength);
			Assert.AreEqual(12, crypto.NonceLength);
		}

		[TestMethod]
		[DataRow(Origin, @"72771304872876d38cfbf36aaabece33a08b68d4649f35de5ed93d3d16d1481e0cf9959111c2c474f51294d72a57dca30e4b9188999885e313ba10d62531d1ffa4454a97d38861169a2b30d9555ba701e55327cb5162b9dd8d9d0815e027d33452a5288641e956ae65860475f64392faec12428e705ba001229563fa15a4ded235039b55b749d9bc6f043398d73863f89f74b875c5824f0c313d3e1e211485f7837fde0c")]
		[DataRow(Origin2, @"1dca4629620bfecd77133488d3e34d9594155982cf514f554f8d0332a4007742979d66ac9524d52d03d82bad6594da985a20b53a52138b5dfc95e9ead17f1e7281568e90111b0b8d26dd11a74eb36457de35505dbb99d91ef4a4848fa9c4dffb1e5938c1a9537b3c09f1bcba762a2f64")]
		public void XChaCha20Poly1305(string str, string encHex)
		{
			TestAEAD(ShadowsocksCrypto.XChaCha20IetfPoly1305Method, Password, str, encHex);

			using var crypto = new XChaChaPoly1305ShadowsocksCrypto(Password);
			Assert.AreEqual(32, crypto.KeyLength);
			Assert.AreEqual(32, crypto.SaltLength);
			Assert.AreEqual(24, crypto.NonceLength);
		}

		[TestMethod]
		[DataRow(Origin, @"a39de9d800b7bef731847766832b7b708489d6adb7eb076777d2c76a462d8bca738c0ef06cfa28f57297989494cf4b6d105c4e1a2b8950f7d28d26d60b3231d75916d0e129ef4e0ef687e1213562e7cad4027a0161c2b59a1542f148c3645bfac72736737f235b059122ac7e17b636ba172de71e53c35d13c441237b443852d7084fcbeaacdba32610f8aae18486f648d72c488a")]
		[DataRow(Origin2, @"59abca4188216e04e4b9329a0cb1748af317d1c2899440e4947960224dc692387d22525b2d9c5244a382230996a9e10ece197744dafc6f06d04219493b2f4d4510308fa0acf03fe0db4093d078fffb74e92f1c2b4580a310ba8792c05c12e821")]
		public void Sm4Gcm(string str, string encHex)
		{
			TestAEAD(ShadowsocksCrypto.Sm4GcmMethod, Password, str, encHex);

			using var crypto = new Sm4GcmShadowsocksCrypto(Password);
			Assert.AreEqual(16, crypto.KeyLength);
			Assert.AreEqual(16, crypto.SaltLength);
			Assert.AreEqual(12, crypto.NonceLength);
		}
	}
}
