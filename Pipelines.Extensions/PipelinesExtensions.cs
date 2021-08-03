using Nerdbank.Streams;
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pipelines.Extensions
{
	public delegate ParseResult HandleReadOnlySequence(ref ReadOnlySequence<byte> buffer);

	public static class PipelinesExtensions
	{
		public static async ValueTask<bool> ReadAsync(
			this PipeReader reader,
			HandleReadOnlySequence func,
			CancellationToken token = default)
		{
			while (true)
			{
				var result = await reader.ReadAsync(token);
				var buffer = result.Buffer;
				try
				{
					var readResult = func(ref buffer);

					if (readResult is ParseResult.Success)
					{
						return true;
					}

					if (readResult is not ParseResult.NeedsMoreData || result.IsCompleted)
					{
						return false;
					}
				}
				finally
				{
					reader.AdvanceTo(buffer.Start, buffer.End);
				}
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async ValueTask<FlushResult> WriteAsync(
			this PipeWriter writer,
			int maxBufferSize,
			Func<Memory<byte>, int> copyTo,
			CancellationToken token = default)
		{
			var memory = writer.GetMemory(maxBufferSize);

			var length = copyTo(memory);

			writer.Advance(length);
			return await writer.FlushAsync(token);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async ValueTask<FlushResult> WriteAsync(this PipeWriter writer, string str, CancellationToken token = default)
		{
			var encoding = Encoding.UTF8;

			var memory = writer.GetMemory(encoding.GetMaxByteCount(str.Length));
			var length = encoding.GetBytes(str, memory.Span);
			writer.Advance(length);

			return await writer.FlushAsync(token);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static IDuplexPipe AsDuplexPipe(this Stream stream, int sizeHint = 0, PipeOptions? pipeOptions = null, CancellationToken cancellationToken = default)
		{
			//TODO .NET6.0
			return stream.UsePipe(sizeHint, pipeOptions, cancellationToken);
		}
	}
}
