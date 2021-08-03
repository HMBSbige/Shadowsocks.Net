using System.Buffers;

namespace Pipelines.Extensions
{
	public delegate ParseResult HandleReadOnlySequence(ref ReadOnlySequence<byte> buffer);
}
