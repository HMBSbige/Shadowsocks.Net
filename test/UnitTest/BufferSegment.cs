using System.Buffers;

namespace UnitTest;

internal class BufferSegment : ReadOnlySequenceSegment<byte>
{
	public static BufferSegment Empty => new(Memory<byte>.Empty);

	public BufferSegment(Memory<byte> memory)
	{
		Memory = memory;
	}

	public BufferSegment Append(Memory<byte> memory)
	{
		BufferSegment segment = new(memory)
		{
			RunningIndex = RunningIndex + Memory.Length
		};
		Next = segment;
		return segment;
	}
}
