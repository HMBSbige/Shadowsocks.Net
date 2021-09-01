using Microsoft;
using Pipelines.Extensions.SocketPipe;
using Pipelines.Extensions.WebSocketPipe;
using System;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Pipelines.Extensions
{
	public static partial class PipelinesExtensions
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

		public static async ValueTask<long> CopyToAsync(
			this PipeReader reader,
			PipeWriter target,
			long size,
			CancellationToken cancellationToken = default)
		{
			Requires.Range(size >= 0, nameof(size), @"size must >=0.");
			var readSize = 0L;

			while (true)
			{
				var result = await reader.ReadAsync(cancellationToken);
				var buffer = result.Buffer;
				if (buffer.Length > size)
				{
					buffer = buffer.Slice(0, size);
				}
				SequencePosition position = buffer.Start;
				SequencePosition consumed = position;

				try
				{
					while (buffer.TryGet(ref position, out var memory))
					{
						var flushResult = await target.WriteAsync(memory, cancellationToken);
						flushResult.ThrowIfCanceled(cancellationToken);

						readSize += memory.Length;
						consumed = position;

						if (flushResult.IsCompleted)
						{
							return readSize;
						}
					}

					consumed = buffer.End;
					size -= buffer.Length;

					if (size <= 0 || result.IsCompleted)
					{
						break;
					}
				}
				finally
				{
					reader.AdvanceTo(consumed);
				}
			}

			return readSize;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void ThrowIfCanceled(this ReadResult result, CancellationToken cancellationToken = default)
		{
			if (!result.IsCanceled)
			{
				return;
			}

			cancellationToken.ThrowIfCancellationRequested();
			throw new OperationCanceledException(@"The PipeReader was canceled.");
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async ValueTask<ReadResult> ReadAndCheckIsCanceledAsync(this PipeReader reader, CancellationToken cancellationToken = default)
		{
			var result = await reader.ReadAsync(cancellationToken);
			result.ThrowIfCanceled(cancellationToken);
			return result;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static PipeReader AsPipeReader(this Socket socket, SocketPipeReaderOptions? options = null)
		{
			return new SocketPipeReader(socket, options ?? SocketPipeReaderOptions.Default);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static PipeReader AsPipeReader(this WebSocket webSocket, WebSocketPipeReaderOptions? options = null)
		{
			return new WebSocketPipeReader(webSocket, options ?? WebSocketPipeReaderOptions.Default);
		}
	}
}
