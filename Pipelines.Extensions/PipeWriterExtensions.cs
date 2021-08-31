using Pipelines.Extensions.SocketPipe;
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pipelines.Extensions
{
	public static partial class PipelinesExtensions
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void Write(this PipeWriter writer, int maxBufferSize, CopyToSpan copyTo)
		{
			var memory = writer.GetSpan(maxBufferSize);

			var length = copyTo(memory);

			writer.Advance(length);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async ValueTask<FlushResult> WriteAsync(
			this PipeWriter writer,
			int maxBufferSize,
			CopyToSpan copyTo,
			CancellationToken token = default)
		{
			writer.Write(maxBufferSize, copyTo);
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

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async ValueTask<FlushResult> WriteAsync(this PipeWriter writer, string str, CancellationToken token = default)
		{
			writer.Write(str);
			return await writer.FlushAsync(token);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async ValueTask<FlushResult> WriteAsync(this PipeWriter writer, ReadOnlySequence<byte> sequence, CancellationToken token = default)
		{
			FlushResult flushResult = default;

			foreach (var memory in sequence)
			{
				writer.Write(memory.Span);
				flushResult = await writer.FlushAndCheckIsCanceledAsync(token);

				if (flushResult.IsCompleted)
				{
					break;
				}
			}

			return flushResult;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void ThrowIfCanceled(this FlushResult flushResult, CancellationToken cancellationToken = default)
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
			flushResult.ThrowIfCanceled(cancellationToken);
			return flushResult;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static PipeWriter AsPipeWriter(this Socket socket, SocketPipeWriterOptions? options = null)
		{
			return new SocketPipeWriter(socket, options ?? SocketPipeWriterOptions.Default);
		}
	}
}
