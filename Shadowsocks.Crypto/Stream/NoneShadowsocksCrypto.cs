using System;

namespace Shadowsocks.Crypto.Stream
{
	public sealed class NoneShadowsocksCrypto : StreamShadowsocksCrypto
	{
		public override int KeyLength => 16;

		public override int IvLength => 0;

		public NoneShadowsocksCrypto(string password) : base(password)
		{
		}

		protected override void InitCipher(bool isEncrypt)
		{
		}

		protected override void UpdateStream(ReadOnlySpan<byte> source, Span<byte> destination)
		{
			source.CopyTo(destination);
		}
	}
}
