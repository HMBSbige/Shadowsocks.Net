using CryptoBase;
using CryptoBase.Abstractions.SymmetricCryptos;
using System;

namespace Shadowsocks.Crypto.AEAD
{
	public class Aes128GcmShadowsocksCrypto : AEADShadowsocksCrypto
	{
		public override int KeyLength => 16;

		public override int SaltLength => 16;

		public Aes128GcmShadowsocksCrypto(string password) : base(password)
		{
		}

		protected override IAEADCrypto CreateCrypto(ReadOnlySpan<byte> key)
		{
			return AEADCryptoCreate.AesGcm(key);
		}
	}
}
