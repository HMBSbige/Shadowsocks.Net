using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pipelines.Extensions
{
	public static partial class PipelinesExtensions
	{
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
		public static void Write(this PipeWriter writer, string str)
		{
			var encoding = Encoding.UTF8;

			var span = writer.GetSpan(encoding.GetMaxByteCount(str.Length));
			var length = encoding.GetBytes(str, span);
			writer.Advance(length);
		}

		public static void Write(this PipeWriter writer, ReadOnlySequence<byte> sequence)
		{
			foreach (var memory in sequence)
			{
				var sourceSpan = memory.Span;
				var targetSpan = writer.GetSpan(sourceSpan.Length);
				sourceSpan.CopyTo(targetSpan);
				writer.Advance(sourceSpan.Length);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async ValueTask<FlushResult> WriteAsync(this PipeWriter writer, string str, CancellationToken token = default)
		{
			writer.Write(str);
			return await writer.FlushAsync(token);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async ValueTask<FlushResult> WriteAsync(this PipeWriter writer, ReadOnlySequence<byte> sequence, CancellationToken token = default)
		{
			writer.Write(sequence);
			return await writer.FlushAsync(token);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void CheckIsCanceled(this FlushResult flushResult, CancellationToken cancellationToken = default)
		{
			if (!flushResult.IsCanceled)
			{
				return;
			}

			cancellationToken.ThrowIfCancellationRequested();
			throw new OperationCanceledException(@"The PipeWriter flush was canceled.");
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async ValueTask<FlushResult> FlushAndCheckIsCanceledAsync(this PipeWriter writer, CancellationToken cancellationToken = default)
		{
			var flushResult = await writer.FlushAsync(cancellationToken);
			flushResult.CheckIsCanceled(cancellationToken);
			return flushResult;
		}
	}
}
