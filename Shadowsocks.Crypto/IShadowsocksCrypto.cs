using System;

namespace Shadowsocks.Crypto
{
	public interface IShadowsocksCrypto : IDisposable
	{
		int AddressBufferLength { get; set; }

		byte[] Key { get; }

		int KeyLength { get; }

		void EncryptTCP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength);
		void DecryptTCP(ReadOnlySpan<byte> source, Span<byte> destination, out int processLength, out int outLength);
		int EncryptUDP(ReadOnlySpan<byte> source, Span<byte> destination);
		int DecryptUDP(ReadOnlySpan<byte> source, Span<byte> destination);
	}
}
