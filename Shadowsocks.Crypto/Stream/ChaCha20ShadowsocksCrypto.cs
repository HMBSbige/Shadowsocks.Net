using CryptoBase;
using CryptoBase.Abstractions.SymmetricCryptos;
using System;

namespace Shadowsocks.Crypto.Stream
{
	public class ChaCha20ShadowsocksCrypto : StreamShadowsocksCrypto
	{
		public override int KeyLength => 32;

		public override int IvLength => 8;

		public ChaCha20ShadowsocksCrypto(string password) : base(password)
		{
		}

		protected override IStreamCrypto CreateCrypto(bool isEncrypt, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
		{
			return StreamCryptoCreate.ChaCha20Original(key, iv);
		}
	}
}
