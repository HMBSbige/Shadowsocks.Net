using Microsoft;
using System.Buffers;

namespace UnitTest;

internal static class TestUtils
{
	public static ReadOnlySequence<byte> GetMultiSegmentSequence(Memory<byte> source, params int[] index)
	{
		Requires.Argument(index.LongLength > 1, nameof(index), @"index length must >1");
		IOrderedEnumerable<int> orderedIndex = index.OrderBy(x => x);

		BufferSegment first = BufferSegment.Empty;
		BufferSegment last = first;
		int length = 0;

		foreach (int i in orderedIndex)
		{
			last = last.Append(source[length..i]);
			length = i;
		}

		last = last.Append(source[length..]);

		ReadOnlySequence<byte> sequence = new(first, 0, last, last.Memory.Length);
		Assert.AreEqual(source.Length, sequence.Length);

		return sequence;
	}

	public static ReadOnlySequence<byte> GetMultiSegmentSequence(params Memory<byte>[] memories)
	{
		Requires.Argument(memories.LongLength > 1, nameof(memories), @"index length must >1");
		BufferSegment first = BufferSegment.Empty;

		BufferSegment last = memories.Aggregate(first, (current, memory) => current.Append(memory));

		ReadOnlySequence<byte> sequence = new(first, 0, last, last.Memory.Length);

		long length = memories.Sum(x => (long)x.Length);
		Assert.AreEqual(length, sequence.Length);

		return sequence;
	}
}
