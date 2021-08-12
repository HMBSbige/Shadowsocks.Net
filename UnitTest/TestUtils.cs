using Microsoft;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Buffers;
using System.Linq;

namespace UnitTest
{
	internal static class TestUtils
	{
		public static ReadOnlySequence<byte> GetMultiSegmentSequence(Memory<byte> source, params int[] index)
		{
			Requires.Argument(index.LongLength > 1, nameof(index), @"index length must >1");
			var orderedIndex = index.OrderBy(x => x);
			var first = new BufferSegment(source[..orderedIndex.First()]);

			var last = first;
			var length = index[0];

			foreach (var i in index.Skip(1))
			{
				last = last.Append(source.Slice(length, i - length));
				length = i;
			}

			last = last.Append(source[length..]);

			var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
			Assert.AreEqual(source.Length, sequence.Length);
			return sequence;
		}
	}
}
