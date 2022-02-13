using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pipelines.Extensions;
using System.Buffers;
using System.Security.Cryptography;

namespace UnitTest;

[TestClass]
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

	[TestMethod]
	public async Task EmptySequenceReadAsync()
	{
		byte[] t = new byte[16];
		Assert.AreEqual(-1, EmptyStream.ReadByte());
		Assert.AreEqual(0, EmptyStream.Read(t));
		Assert.AreEqual(0, await EmptyStream.ReadAsync(t));
		Assert.AreEqual(0, await EmptyStream.ReadAsync(t, 0, t.Length));
	}

	[TestMethod]
	public void EmptySequenceWrite()
	{
		Stream stream = EmptyStream;
		byte[] b = new byte[16];

		// ReSharper disable AccessToDisposedClosure
		Assert.ThrowsException<NotSupportedException>(() => stream.WriteByte(b[0]));
		Assert.ThrowsException<NotSupportedException>(() => stream.Write(b, 0, b.Length));
		Assert.ThrowsException<NotSupportedException>(() => stream.Write(b));
		// ReSharper restore AccessToDisposedClosure

		stream.Dispose();
		Assert.ThrowsException<ObjectDisposedException>(() => stream.WriteByte(b[0]));
		Assert.ThrowsException<ObjectDisposedException>(() => stream.Write(b, 0, b.Length));
		Assert.ThrowsException<ObjectDisposedException>(() => stream.Write(b));
	}

	[TestMethod]
	public async Task EmptySequenceFlushAsync()
	{
		Stream stream = EmptyStream;
		Assert.ThrowsException<NotSupportedException>(() => stream.Flush());
		await stream.DisposeAsync();
		Assert.ThrowsException<ObjectDisposedException>(() => stream.Flush());
	}

	[TestMethod]
	public void EmptySequenceSeek()
	{
		Stream stream = EmptyStream;
		Assert.AreEqual(0, stream.Seek(0, SeekOrigin.Begin));
		Assert.AreEqual(0, stream.Seek(0, SeekOrigin.Current));
		Assert.AreEqual(0, stream.Seek(0, SeekOrigin.End));

		stream.Dispose();
		Assert.ThrowsException<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
	}

	[TestMethod]
	public void CanRead()
	{
		Stream stream = EmptyStream;
		Assert.IsTrue(stream.CanRead);
		stream.Dispose();
		Assert.IsFalse(stream.CanRead);
	}

	[TestMethod]
	public void CanSeek()
	{
		Stream stream = EmptyStream;
		Assert.IsTrue(stream.CanSeek);
		stream.Dispose();
		Assert.IsFalse(stream.CanSeek);
	}

	[TestMethod]
	public void CanWrite()
	{
		Stream stream = EmptyStream;
		Assert.IsFalse(stream.CanWrite);
		stream.Dispose();
		Assert.IsFalse(stream.CanWrite);
	}

	[TestMethod]
	public void Length()
	{
		Stream stream = EmptyStream;
		Assert.AreEqual(0, stream.Length);
		stream.Dispose();
		// ReSharper disable once AccessToModifiedClosure
		Assert.ThrowsException<ObjectDisposedException>(() => stream.Length);

		stream = MultiSegmentSequence.AsStream();
		Assert.AreEqual(MultiSegmentSequence.Length, stream.Length);
	}

	[TestMethod]
	public void CanTimeout()
	{
		Stream stream = EmptyStream;
		Assert.IsFalse(stream.CanTimeout);
		stream.Dispose();
		Assert.IsFalse(stream.CanTimeout);
	}

	[TestMethod]
	public void SetLength()
	{
		Stream stream = EmptyStream;
		// ReSharper disable once AccessToDisposedClosure
		Assert.ThrowsException<NotSupportedException>(() => stream.SetLength(0));
		stream.Dispose();
		Assert.ThrowsException<ObjectDisposedException>(() => stream.SetLength(0));
	}

	[TestMethod]
	public void Position()
	{
		Stream stream = EmptyStream;
		Assert.ThrowsException<ArgumentOutOfRangeException>(() => stream.Position = 1);

		Stream singleStream = SingleSegmentSequence.AsStream();
		Stream multiStream = MultiSegmentSequence.AsStream();
		int n = RandomNumberGenerator.GetInt32(2, 100);

		Assert.AreEqual(0, singleStream.Position);
		++singleStream.Position;
		Assert.AreEqual(1, singleStream.Position);
		singleStream.Position += n;
		Assert.AreEqual(1 + n, singleStream.Position);

		for (int i = 0; i < MultiSegmentSequence.Length; ++i)
		{
			multiStream.Position = i;
			Assert.AreNotEqual(-1, multiStream.ReadByte());
			Assert.AreEqual(i + 1, multiStream.Position);
		}

		multiStream.Position = 0;
		Assert.AreNotEqual(-1, multiStream.ReadByte());
		Assert.AreEqual(1, multiStream.Position);

		multiStream.Position = MultiSegmentSequence.Length;
		Assert.AreEqual(-1, multiStream.ReadByte());

		// ReSharper disable AccessToDisposedClosure
		Assert.ThrowsException<ArgumentOutOfRangeException>(() => multiStream.Position = MultiSegmentSequence.Length + 1);
		Assert.ThrowsException<ArgumentOutOfRangeException>(() => multiStream.Position = -1);
		// ReSharper restore AccessToDisposedClosure

		multiStream.Dispose();
		Assert.ThrowsException<ObjectDisposedException>(() => multiStream.Position = 0);
	}

	[TestMethod]
	public void Seek()
	{
		Stream stream = MultiSegmentSequence.AsStream();

		for (int i = 0; i < MultiSegmentSequence.Length; ++i)
		{
			Assert.AreEqual(i, stream.Seek(i, SeekOrigin.Begin));
			Assert.AreEqual(i, stream.Position);
			Assert.AreEqual(GetByte(i), stream.ReadByte());
		}
		Assert.ThrowsException<ArgumentOutOfRangeException>(() => stream.Seek(-1, SeekOrigin.Begin));

		for (int n = 0; n < stream.Length; ++n)
		{
			for (int i = 0; i < stream.Length - n; ++i)
			{
				stream.Position = n;
				Assert.AreEqual(n + i, stream.Seek(i, SeekOrigin.Current));
				Assert.AreEqual(GetByte(n + i), stream.ReadByte());
			}
			for (int i = 0; i < n; ++i)
			{
				stream.Position = n;
				Assert.AreEqual(n - i, stream.Seek(-i, SeekOrigin.Current));
				Assert.AreEqual(GetByte(n - i), stream.ReadByte());
			}
		}
		stream.Position = MultiSegmentSequence.Length;
		Assert.ThrowsException<ArgumentOutOfRangeException>(() => stream.Seek(1, SeekOrigin.Current));
		stream.Position = 0;
		Assert.ThrowsException<ArgumentOutOfRangeException>(() => stream.Seek(-1, SeekOrigin.Current));

		for (int i = 0; i < stream.Length; ++i)
		{
			Assert.AreEqual(MultiSegmentSequence.Length - i, stream.Seek(-i, SeekOrigin.End));
			Assert.AreEqual(GetByte(MultiSegmentSequence.Length - i), stream.ReadByte());
		}
		Assert.ThrowsException<ArgumentOutOfRangeException>(() => stream.Seek(1, SeekOrigin.End));

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
