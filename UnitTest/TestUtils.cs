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

			var first = BufferSegment.Empty;
			var last = first;
			var length = 0;

			foreach (var i in orderedIndex)
			{
				last = last.Append(source.Slice(length, i - length));
				length = i;
			}

			last = last.Append(source[length..]);

			var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
			Assert.AreEqual(source.Length, sequence.Length);

			return sequence;
		}

		public static ReadOnlySequence<byte> GetMultiSegmentSequence(params Memory<byte>[] memories)
		{
			Requires.Argument(memories.LongLength > 1, nameof(memories), @"index length must >1");
			var first = BufferSegment.Empty;

			var last = memories.Aggregate(first, (current, memory) => current.Append(memory));

			var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);

			var length = memories.Sum(x => (long)x.Length);
			Assert.AreEqual(length, sequence.Length);

			return sequence;
		}
	}
}
