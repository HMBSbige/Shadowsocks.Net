using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;

namespace Pipelines.Extensions
{
	public static class StreamExtensions
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static Stream AsStream(this ReadOnlySequence<byte> sequence)
		{
			return new ReadOnlySequenceStream(sequence);
		}
	}
}
