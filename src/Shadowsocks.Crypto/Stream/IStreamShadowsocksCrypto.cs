using CryptoBase.Abstractions;

namespace Shadowsocks.Crypto.Stream;

public interface IStreamShadowsocksCrypto : IShadowsocksCrypto, ICanReset
{
	byte[] Iv { get; }

	int IvLength { get; }

	void SetIv(ReadOnlySpan<byte> iv);
}
