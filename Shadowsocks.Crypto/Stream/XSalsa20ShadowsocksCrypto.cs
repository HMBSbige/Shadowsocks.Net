using CryptoBase;
using CryptoBase.Abstractions.SymmetricCryptos;
using System;

namespace Shadowsocks.Crypto.Stream
{
	public class XSalsa20ShadowsocksCrypto : CryptoBaseStreamShadowsocksCrypto
	{
		public override int KeyLength => 32;

		public override int IvLength => 24;

		public XSalsa20ShadowsocksCrypto(string password) : base(password)
		{
		}

		protected override IStreamCrypto CreateCrypto(bool isEncrypt, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
		{
			return StreamCryptoCreate.XSalsa20(key, iv);
		}
	}
}
