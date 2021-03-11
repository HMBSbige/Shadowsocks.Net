using CryptoBase;
using CryptoBase.Abstractions.SymmetricCryptos;
using System;

namespace Shadowsocks.Crypto.Stream
{
	public class Rc4ShadowsocksCrypto : StreamShadowsocksCrypto
	{
		public override int KeyLength => 16;
		public override int IvLength => 0;

		private IStreamCrypto? _crypto;

		public Rc4ShadowsocksCrypto(string password) : base(password)
		{
		}

		protected override void InitCipher(bool isEncrypt)
		{
			if (_crypto is null)
			{
				_crypto = StreamCryptoCreate.Rc4(Key.AsSpan(0, KeyLength));
			}
			else
			{
				_crypto.Reset();
			}
		}

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
