using Socks5;
using System.Buffers;

namespace UnitTest;

public class IsClientHeaderTest
{
	[Test]
	public async Task ValidHeader(CancellationToken cancellationToken)
	{
		byte[] data = [0x05, 0x01, 0x00]; // VER=05, NMETHODS=1, METHOD=00
		ReadOnlySequence<byte> seq = new(data);

		await Assert.That(seq.IsSocks5Header()).IsTrue();
	}

	[Test]
	public async Task WrongVersion(CancellationToken cancellationToken)
	{
		byte[] data = [0x04, 0x01, 0x00];
		ReadOnlySequence<byte> seq = new(data);

		await Assert.That(seq.IsSocks5Header()).IsFalse();
	}

	[Test]
	public async Task Empty(CancellationToken cancellationToken)
	{
		ReadOnlySequence<byte> seq = ReadOnlySequence<byte>.Empty;

		await Assert.That(seq.IsSocks5Header()).IsFalse();
	}
}
