using CryptoBase;
using CryptoBase.Abstractions.SymmetricCryptos;
using System;

namespace Shadowsocks.Crypto.Stream
{
	public class XChaCha20ShadowsocksCrypto : StreamShadowsocksCrypto
	{
		public override int KeyLength => 32;

		public override int IvLength => 24;

		public XChaCha20ShadowsocksCrypto(string password) : base(password)
		{
		}

		protected override IStreamCrypto CreateCrypto(bool isEncrypt, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
		{
			return StreamCryptoCreate.XChaCha20(key, iv);
		}
	}
}
