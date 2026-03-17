using Pipelines.Extensions;
using System.Buffers;
using System.Security.Cryptography;

namespace UnitTest;

public class ReadOnlySequenceStreamTest
{
	private static readonly ReadOnlySequence<byte> SingleSegmentSequence;
	private static readonly ReadOnlySequence<byte> MultiSegmentSequence;

	private static Stream EmptyStream => ReadOnlySequence<byte>.Empty.AsStream();

	static ReadOnlySequenceStreamTest()
	{
		byte[] x = new byte[114];
		RandomNumberGenerator.Fill(x);
		SingleSegmentSequence = new ReadOnlySequence<byte>(x);

		byte[] m = new byte[114];
		RandomNumberGenerator.Fill(m);
		MultiSegmentSequence = TestUtils.GetMultiSegmentSequence(m, 15, 100);
	}

	[Test]
	public async Task EmptySequenceReadAsync(CancellationToken cancellationToken)
	{
		byte[] t = new byte[16];
		await Assert.That(EmptyStream.ReadByte()).IsEqualTo(-1);
		await Assert.That(EmptyStream.Read(t)).IsEqualTo(0);
		await Assert.That(await EmptyStream.ReadAsync(t)).IsEqualTo(0);
		await Assert.That(await EmptyStream.ReadAsync(t, 0, t.Length)).IsEqualTo(0);
	}

	[Test]
	public async Task EmptySequenceWrite(CancellationToken cancellationToken)
	{
		Stream stream = EmptyStream;
		byte[] b = new byte[16];

		// ReSharper disable AccessToDisposedClosure
		await Assert.That(() => stream.WriteByte(b[0])).ThrowsExactly<NotSupportedException>();
		await Assert.That(() => stream.Write(b, 0, b.Length)).ThrowsExactly<NotSupportedException>();
		await Assert.That(() => stream.Write(b)).ThrowsExactly<NotSupportedException>();
		// ReSharper restore AccessToDisposedClosure

		await stream.DisposeAsync();
		await Assert.That(() => stream.WriteByte(b[0])).ThrowsExactly<ObjectDisposedException>();
		await Assert.That(() => stream.Write(b, 0, b.Length)).ThrowsExactly<ObjectDisposedException>();
		await Assert.That(() => stream.Write(b)).ThrowsExactly<ObjectDisposedException>();
	}

	[Test]
	public async Task EmptySequenceFlush(CancellationToken cancellationToken)
	{
		Stream stream = EmptyStream;
		stream.Flush();
		await stream.FlushAsync();
		await stream.DisposeAsync();
		await Assert.That(() => stream.Flush()).ThrowsExactly<ObjectDisposedException>();
		await Assert.That(() => stream.FlushAsync()).ThrowsExactly<ObjectDisposedException>();
	}

	[Test]
	public async Task EmptySequenceSeek(CancellationToken cancellationToken)
	{
		Stream stream = EmptyStream;
		await Assert.That(stream.Seek(0, SeekOrigin.Begin)).IsEqualTo(0);
		await Assert.That(stream.Seek(0, SeekOrigin.Current)).IsEqualTo(0);
		await Assert.That(stream.Seek(0, SeekOrigin.End)).IsEqualTo(0);

		await stream.DisposeAsync();
		await Assert.That(() => stream.Seek(0, SeekOrigin.Begin)).ThrowsExactly<ObjectDisposedException>();
	}

	[Test]
	public async Task CanRead(CancellationToken cancellationToken)
	{
		Stream stream = EmptyStream;
		await Assert.That(stream.CanRead).IsTrue();
		await stream.DisposeAsync();
		await Assert.That(stream.CanRead).IsFalse();
	}

	[Test]
	public async Task CanSeek(CancellationToken cancellationToken)
	{
		Stream stream = EmptyStream;
		await Assert.That(stream.CanSeek).IsTrue();
		await stream.DisposeAsync();
		await Assert.That(stream.CanSeek).IsFalse();
	}

	[Test]
	public async Task CanWrite(CancellationToken cancellationToken)
	{
		Stream stream = EmptyStream;
		await Assert.That(stream.CanWrite).IsFalse();
		await stream.DisposeAsync();
		await Assert.That(stream.CanWrite).IsFalse();
	}

	[Test]
	public async Task Length(CancellationToken cancellationToken)
	{
		Stream stream = EmptyStream;
		await Assert.That(stream.Length).IsEqualTo(0);
		await stream.DisposeAsync();
		// ReSharper disable once AccessToModifiedClosure
		await Assert.That(() => stream.Length).ThrowsExactly<ObjectDisposedException>();

		stream = MultiSegmentSequence.AsStream();
		await Assert.That(stream.Length).IsEqualTo(MultiSegmentSequence.Length);
	}

	[Test]
	public async Task CanTimeout(CancellationToken cancellationToken)
	{
		Stream stream = EmptyStream;
		await Assert.That(stream.CanTimeout).IsFalse();
		await stream.DisposeAsync();
		await Assert.That(stream.CanTimeout).IsFalse();
	}

	[Test]
	public async Task SetLength(CancellationToken cancellationToken)
	{
		Stream stream = EmptyStream;
		// ReSharper disable once AccessToDisposedClosure
		await Assert.That(() => stream.SetLength(0)).ThrowsExactly<NotSupportedException>();
		await stream.DisposeAsync();
		await Assert.That(() => stream.SetLength(0)).ThrowsExactly<ObjectDisposedException>();
	}

	[Test]
	public async Task Position(CancellationToken cancellationToken)
	{
		Stream stream = EmptyStream;
		await Assert.That(() => stream.Position = 1).ThrowsExactly<ArgumentOutOfRangeException>();

		Stream singleStream = SingleSegmentSequence.AsStream();
		Stream multiStream = MultiSegmentSequence.AsStream();
		int n = RandomNumberGenerator.GetInt32(2, 100);

		await Assert.That(singleStream.Position).IsEqualTo(0);
		++singleStream.Position;
		await Assert.That(singleStream.Position).IsEqualTo(1);
		singleStream.Position += n;
		await Assert.That(singleStream.Position).IsEqualTo(1 + n);

		for (int i = 0; i < MultiSegmentSequence.Length; ++i)
		{
			multiStream.Position = i;
			await Assert.That(multiStream.ReadByte()).IsNotEqualTo(-1);
			await Assert.That(multiStream.Position).IsEqualTo(i + 1);
		}

		multiStream.Position = 0;
		await Assert.That(multiStream.ReadByte()).IsNotEqualTo(-1);
		await Assert.That(multiStream.Position).IsEqualTo(1);

		multiStream.Position = MultiSegmentSequence.Length;
		await Assert.That(multiStream.ReadByte()).IsEqualTo(-1);

		// ReSharper disable AccessToDisposedClosure
		await Assert.That(() => multiStream.Position = MultiSegmentSequence.Length + 1).ThrowsExactly<ArgumentOutOfRangeException>();
		await Assert.That(() => multiStream.Position = -1).ThrowsExactly<ArgumentOutOfRangeException>();
		// ReSharper restore AccessToDisposedClosure

		await multiStream.DisposeAsync();
		await Assert.That(() => multiStream.Position = 0).ThrowsExactly<ObjectDisposedException>();
	}

	[Test]
	public async Task ReadByte(CancellationToken cancellationToken)
	{
		await using Stream stream = SingleSegmentSequence.AsStream();

		for (int i = 0; i < SingleSegmentSequence.Length; ++i)
		{
			int b = stream.ReadByte();
			await Assert.That(b).IsNotEqualTo(-1);
			await Assert.That((byte)b).IsEqualTo(SingleSegmentSequence.Slice(i, 1).First.Span[0]);
		}

		await Assert.That(stream.ReadByte()).IsEqualTo(-1);

		Stream multiStream = MultiSegmentSequence.AsStream();

		for (int i = 0; i < MultiSegmentSequence.Length; ++i)
		{
			int b = multiStream.ReadByte();
			await Assert.That(b).IsNotEqualTo(-1);

			SequenceReader<byte> reader = new(MultiSegmentSequence.Slice(i));
			reader.TryRead(out byte expected);
			await Assert.That((byte)b).IsEqualTo(expected);
		}

		await Assert.That(multiStream.ReadByte()).IsEqualTo(-1);

		await multiStream.DisposeAsync();
		await Assert.That(() => multiStream.ReadByte()).ThrowsExactly<ObjectDisposedException>();
	}

	[Test]
	public async Task CopyToAsync(CancellationToken cancellationToken)
	{
		// Single segment
		await using MemoryStream singleDest = new();
		await using Stream singleStream = SingleSegmentSequence.AsStream();
		await singleStream.CopyToAsync(singleDest);
		await Assert.That(singleDest.Length).IsEqualTo(SingleSegmentSequence.Length);
		await Assert.That(singleDest.ToArray().SequenceEqual(SingleSegmentSequence.ToArray())).IsTrue();

		// Multi segment
		await using MemoryStream multiDest = new();
		await using Stream multiStream = MultiSegmentSequence.AsStream();
		await multiStream.CopyToAsync(multiDest);
		await Assert.That(multiDest.Length).IsEqualTo(MultiSegmentSequence.Length);
		await Assert.That(multiDest.ToArray().SequenceEqual(MultiSegmentSequence.ToArray())).IsTrue();

		// Partial copy (advance position first)
		await using MemoryStream partialDest = new();
		await using Stream partialStream = MultiSegmentSequence.AsStream();
		partialStream.Position = 10;
		await partialStream.CopyToAsync(partialDest);
		await Assert.That(partialDest.Length).IsEqualTo(MultiSegmentSequence.Length - 10);
		await Assert.That(partialDest.ToArray().SequenceEqual(MultiSegmentSequence.Slice(10).ToArray())).IsTrue();
		await Assert.That(partialStream.Position).IsEqualTo(MultiSegmentSequence.Length);

		// Empty
		await using MemoryStream emptyDest = new();
		await EmptyStream.CopyToAsync(emptyDest);
		await Assert.That(emptyDest.Length).IsEqualTo(0);

		// Disposed
		Stream disposed = MultiSegmentSequence.AsStream();
		await disposed.DisposeAsync();
		await Assert.That(() => disposed.CopyToAsync(new MemoryStream())).ThrowsExactly<ObjectDisposedException>();
	}

	[Test]
	public async Task Seek(CancellationToken cancellationToken)
	{
		Stream stream = MultiSegmentSequence.AsStream();

		for (int i = 0; i < MultiSegmentSequence.Length; ++i)
		{
			await Assert.That(stream.Seek(i, SeekOrigin.Begin)).IsEqualTo(i);
			await Assert.That(stream.Position).IsEqualTo(i);
			await Assert.That(stream.ReadByte()).IsEqualTo(GetByte(i));
		}

		await Assert.That(() => stream.Seek(-1, SeekOrigin.Begin)).ThrowsExactly<ArgumentOutOfRangeException>();

		for (int n = 0; n < stream.Length; ++n)
		{
			for (int i = 0; i < stream.Length - n; ++i)
			{
				stream.Position = n;
				await Assert.That(stream.Seek(i, SeekOrigin.Current)).IsEqualTo(n + i);
				await Assert.That(stream.ReadByte()).IsEqualTo(GetByte(n + i));
			}

			for (int i = 0; i < n; ++i)
			{
				stream.Position = n;
				await Assert.That(stream.Seek(-i, SeekOrigin.Current)).IsEqualTo(n - i);
				await Assert.That(stream.ReadByte()).IsEqualTo(GetByte(n - i));
			}
		}

		stream.Position = MultiSegmentSequence.Length;
		await Assert.That(() => stream.Seek(1, SeekOrigin.Current)).ThrowsExactly<ArgumentOutOfRangeException>();
		stream.Position = 0;
		await Assert.That(() => stream.Seek(-1, SeekOrigin.Current)).ThrowsExactly<ArgumentOutOfRangeException>();

		for (int i = 0; i < stream.Length; ++i)
		{
			await Assert.That(stream.Seek(-i, SeekOrigin.End)).IsEqualTo(MultiSegmentSequence.Length - i);
			await Assert.That(stream.ReadByte()).IsEqualTo(GetByte(MultiSegmentSequence.Length - i));
		}

		await Assert.That(() => stream.Seek(1, SeekOrigin.End)).ThrowsExactly<ArgumentOutOfRangeException>();
		return;

		int GetByte(long position)
		{
			SequenceReader<byte> reader = new(MultiSegmentSequence.Slice(position));

			if (reader.TryRead(out byte b))
			{
				return b;
			}

			return -1;
		}
	}
}
