using System;
using System.Buffers;

namespace Shadowsocks.Crypto
{
	public interface IShadowsocksCrypto : IDisposable
	{
		byte[] Key { get; }

		int KeyLength { get; }

		void EncryptTCP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength);
		int DecryptTCP(ref ReadOnlySequence<byte> source, Span<byte> destination);
		int EncryptUDP(ReadOnlySpan<byte> source, Span<byte> destination);
		int DecryptUDP(ReadOnlySpan<byte> source, Span<byte> destination);
	}
}
