using CryptoBase.Abstractions.SymmetricCryptos;
using CryptoBase.SymmetricCryptos.StreamCryptos;
using System;

namespace Shadowsocks.Crypto.Stream
{
	public class Salsa20ShadowsocksCrypto : StreamShadowsocksCrypto
	{
		public override int KeyLength => 32;

		public override int IvLength => 8;

		public Salsa20ShadowsocksCrypto(string password) : base(password)
		{
		}

		protected override IStreamCrypto CreateCrypto(bool isEncrypt, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
		{
			return StreamCryptoCreate.Salsa20(key, iv);
		}
	}
}
