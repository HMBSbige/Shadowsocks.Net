using CryptoBase;
using CryptoBase.Abstractions.SymmetricCryptos;
using System;

namespace Shadowsocks.Crypto.Stream
{
	public class Aes128CtrShadowsocksCrypto : StreamShadowsocksCrypto
	{
		public override int KeyLength => 16;

		public override int IvLength => 16;

		public Aes128CtrShadowsocksCrypto(string password) : base(password)
		{
		}

		protected override IStreamCrypto CreateCrypto(bool isEncrypt, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
		{
			return StreamCryptoCreate.AesCtr(key, iv);
		}
	}
}
