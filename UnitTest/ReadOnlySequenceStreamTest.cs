using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pipelines.Extensions;
using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace UnitTest
{
	[TestClass]
	public class ReadOnlySequenceStreamTest
	{
		private static readonly ReadOnlySequence<byte> SingleSegmentSequence;
		private static readonly ReadOnlySequence<byte> MultiSegmentSequence;
		private static Stream EmptyStream => ReadOnlySequence<byte>.Empty.AsStream();

		static ReadOnlySequenceStreamTest()
		{
			var x = new byte[114];
			RandomNumberGenerator.Fill(x);
			SingleSegmentSequence = new ReadOnlySequence<byte>(x);

			var m = new byte[114];
			RandomNumberGenerator.Fill(m);
			MultiSegmentSequence = TestUtils.GetMultiSegmentSequence(m, 15, 100);
		}

		[TestMethod]
		public async Task EmptySequenceReadAsync()
		{
			var t = new byte[16];
			Assert.AreEqual(-1, EmptyStream.ReadByte());
			Assert.AreEqual(0, EmptyStream.Read(t));
			Assert.AreEqual(0, await EmptyStream.ReadAsync(t));
			Assert.AreEqual(0, await EmptyStream.ReadAsync(t, 0, t.Length));
		}

		[TestMethod]
		public void EmptySequenceWrite()
		{
			var stream = EmptyStream;
			var b = new byte[16];

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
			var stream = EmptyStream;
			Assert.ThrowsException<NotSupportedException>(() => stream.Flush());
			await stream.DisposeAsync();
			Assert.ThrowsException<ObjectDisposedException>(() => stream.Flush());
		}

		[TestMethod]
		public void EmptySequenceSeek()
		{
			var stream = EmptyStream;
			Assert.AreEqual(0, stream.Seek(0, SeekOrigin.Begin));
			Assert.AreEqual(0, stream.Seek(0, SeekOrigin.Current));
			Assert.AreEqual(0, stream.Seek(0, SeekOrigin.End));

			stream.Dispose();
			Assert.ThrowsException<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
		}

		[TestMethod]
		public void CanRead()
		{
			var stream = EmptyStream;
			Assert.IsTrue(stream.CanRead);
			stream.Dispose();
			Assert.IsFalse(stream.CanRead);
		}

		[TestMethod]
		public void CanSeek()
		{
			var stream = EmptyStream;
			Assert.IsTrue(stream.CanSeek);
			stream.Dispose();
			Assert.IsFalse(stream.CanSeek);
		}

		[TestMethod]
		public void CanWrite()
		{
			var stream = EmptyStream;
			Assert.IsFalse(stream.CanWrite);
			stream.Dispose();
			Assert.IsFalse(stream.CanWrite);
		}

		[TestMethod]
		public void Length()
		{
			var stream = EmptyStream;
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
			var stream = EmptyStream;
			Assert.IsFalse(stream.CanTimeout);
			stream.Dispose();
			Assert.IsFalse(stream.CanTimeout);
		}

		[TestMethod]
		public void SetLength()
		{
			var stream = EmptyStream;
			// ReSharper disable once AccessToDisposedClosure
			Assert.ThrowsException<NotSupportedException>(() => stream.SetLength(0));
			stream.Dispose();
			Assert.ThrowsException<ObjectDisposedException>(() => stream.SetLength(0));
		}

		[TestMethod]
		public void Position()
		{
			var stream = EmptyStream;
			Assert.ThrowsException<ArgumentOutOfRangeException>(() => stream.Position = 1);

			var singleStream = SingleSegmentSequence.AsStream();
			var multiStream = MultiSegmentSequence.AsStream();
			var n = RandomNumberGenerator.GetInt32(2, 100);

			Assert.AreEqual(0, singleStream.Position);
			++singleStream.Position;
			Assert.AreEqual(1, singleStream.Position);
			singleStream.Position += n;
			Assert.AreEqual(1 + n, singleStream.Position);

			for (var i = 0; i < MultiSegmentSequence.Length; ++i)
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
			var stream = MultiSegmentSequence.AsStream();

			for (var i = 0; i < MultiSegmentSequence.Length; ++i)
			{
				Assert.AreEqual(i, stream.Seek(i, SeekOrigin.Begin));
				Assert.AreEqual(i, stream.Position);
				Assert.AreEqual(GetByte(i), stream.ReadByte());
			}
			Assert.ThrowsException<ArgumentOutOfRangeException>(() => stream.Seek(-1, SeekOrigin.Begin));

			for (var n = 0; n < stream.Length; ++n)
			{
				for (var i = 0; i < stream.Length - n; ++i)
				{
					stream.Position = n;
					Assert.AreEqual(n + i, stream.Seek(i, SeekOrigin.Current));
					Assert.AreEqual(GetByte(n + i), stream.ReadByte());
				}
				for (var i = 0; i < n; ++i)
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

			for (var i = 0; i < stream.Length; ++i)
			{
				Assert.AreEqual(MultiSegmentSequence.Length - i, stream.Seek(-i, SeekOrigin.End));
				Assert.AreEqual(GetByte(MultiSegmentSequence.Length - i), stream.ReadByte());
			}
			Assert.ThrowsException<ArgumentOutOfRangeException>(() => stream.Seek(1, SeekOrigin.End));

			int GetByte(long position)
			{
				var reader = new SequenceReader<byte>(MultiSegmentSequence.Slice(position));
				if (reader.TryRead(out var b))
				{
					return b;
				}
				return -1;
			}
		}
	}
}
