using Pipelines.Extensions.SocketPipe;
using Pipelines.Extensions.WebSocketPipe;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Pipelines.Extensions;

public static partial class PipelinesExtensions
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void Write(this PipeWriter writer, int maxBufferSize, CopyToSpan copyTo)
	{
		Span<byte> memory = writer.GetSpan(maxBufferSize);

		int length = copyTo(memory);

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
		Encoding encoding = Encoding.UTF8;

		Span<byte> span = writer.GetSpan(encoding.GetMaxByteCount(str.Length));
		int length = encoding.GetBytes(str, span);
		writer.Advance(length);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static async ValueTask<FlushResult> WriteAsync(this PipeWriter writer, string str, CancellationToken token = default)
	{
		writer.Write(str);
		return await writer.FlushAsync(token);
	}

	public static async ValueTask<FlushResult> WriteAsync(this PipeWriter writer, ReadOnlySequence<byte> sequence, CancellationToken token = default)
	{
		FlushResult flushResult = default;

		foreach (ReadOnlyMemory<byte> memory in sequence)
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
		FlushResult flushResult = await writer.FlushAsync(cancellationToken);
		flushResult.ThrowIfCanceled(cancellationToken);
		return flushResult;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static PipeWriter AsPipeWriter(this Stream stream, StreamPipeWriterOptions? options = null)
	{
		return PipeWriter.Create(stream, options);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static PipeWriter AsPipeWriter(this Socket socket, SocketPipeWriterOptions? options = null)
	{
		return new SocketPipeWriter(socket, options ?? SocketPipeWriterOptions.Default);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static PipeWriter AsPipeWriter(this WebSocket webSocket, WebSocketPipeWriterOptions? options = null)
	{
		return new WebSocketPipeWriter(webSocket, options ?? WebSocketPipeWriterOptions.Default);
	}
}
