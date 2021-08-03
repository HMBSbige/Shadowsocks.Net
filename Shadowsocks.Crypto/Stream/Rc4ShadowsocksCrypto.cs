using CryptoBase.Abstractions.SymmetricCryptos;
using CryptoBase.SymmetricCryptos.StreamCryptos;
using System;

namespace Shadowsocks.Crypto.Stream
{
	public class Rc4ShadowsocksCrypto : StreamShadowsocksCrypto
	{
		public override int KeyLength => 16;
		public override int IvLength => 0;

		public Rc4ShadowsocksCrypto(string password) : base(password)
		{
		}

		protected override void InitCipher(bool isEncrypt)
		{
			if (Crypto is null)
			{
				Crypto = CreateCrypto(isEncrypt, KeySpan, IvSpan);
			}
			else
			{
				Crypto.Reset();
			}
		}

		protected override IStreamCrypto CreateCrypto(bool isEncrypt, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
		{
			return StreamCryptoCreate.Rc4(key);
		}
	}
}
