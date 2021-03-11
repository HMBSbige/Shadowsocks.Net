using CryptoBase.Abstractions.SymmetricCryptos;
using System;

namespace Shadowsocks.Crypto.Stream
{
	public abstract class CryptoBaseStreamShadowsocksCrypto : StreamShadowsocksCrypto
	{
		private IStreamCrypto? _crypto;

		protected CryptoBaseStreamShadowsocksCrypto(string password) : base(password)
		{
		}

		protected override void InitCipher(bool isEncrypt)
		{
			_crypto?.Dispose();
			_crypto = CreateCrypto(isEncrypt, Key.AsSpan(0, KeyLength), Iv.AsSpan(0, IvLength));
		}

		protected abstract IStreamCrypto CreateCrypto(bool isEncrypt, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv);

		protected override void UpdateStream(ReadOnlySpan<byte> source, Span<byte> destination)
		{
			_crypto!.Update(source, destination);
		}

		public override void Dispose()
		{
			base.Dispose();
			_crypto?.Dispose();
		}
	}
}
