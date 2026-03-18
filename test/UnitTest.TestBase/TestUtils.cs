using System.Buffers;
using System.Diagnostics;

namespace UnitTest.TestBase;

public static class TestUtils
{
	public static ReadOnlySequence<byte> GetMultiSegmentSequence(Memory<byte> source, params int[] index)
	{
		if (index.LongLength <= 1)
		{
			throw new ArgumentException(@"index length must >1", nameof(index));
		}

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
		Debug.Assert(source.Length == sequence.Length, $"Expected {source.Length} but got {sequence.Length}");

		return sequence;
	}

	public static ReadOnlySequence<byte> GetMultiSegmentSequence(params Memory<byte>[] memories)
	{
		if (memories.LongLength <= 1)
		{
			throw new ArgumentException(@"index length must >1", nameof(memories));
		}

		BufferSegment first = BufferSegment.Empty;

		BufferSegment last = memories.Aggregate(first, (current, memory) => current.Append(memory));

		ReadOnlySequence<byte> sequence = new(first, 0, last, last.Memory.Length);

		long length = memories.Sum(x => (long)x.Length);
		Debug.Assert(length == sequence.Length, $"Expected {length} but got {sequence.Length}");

		return sequence;
	}
}
