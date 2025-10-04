using CryptoBase.Abstractions.SymmetricCryptos;

namespace Shadowsocks.Crypto.Stream;

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

	protected override IStreamCrypto CreateCrypto(bool isEncrypt, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
	{
		return default!;
	}

	protected override void UpdateStream(IStreamCrypto crypto, ReadOnlySpan<byte> source, Span<byte> destination)
	{
		source.CopyTo(destination);
	}
}
