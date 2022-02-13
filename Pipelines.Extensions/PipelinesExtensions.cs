using Microsoft;
using Pipelines.Extensions.SocketPipe;
using Pipelines.Extensions.WebSocketPipe;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;

namespace Pipelines.Extensions;

public static partial class PipelinesExtensions
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static async ValueTask LinkToAsync(this IDuplexPipe pipe1, IDuplexPipe pipe2, CancellationToken token = default)
	{
		Task a = pipe1.Input.CopyToAsync(pipe2.Output, token);
		Task b = pipe2.Input.CopyToAsync(pipe1.Output, token);

		await Task.WhenAll(a, b);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static IDuplexPipe AsDuplexPipe(
		this Stream stream,
		StreamPipeReaderOptions? readerOptions = null,
		StreamPipeWriterOptions? writerOptions = null)
	{
		Requires.Argument(stream.CanRead, nameof(stream), @"Stream is not readable.");
		Requires.Argument(stream.CanWrite, nameof(stream), @"Stream is not writable.");

		PipeReader reader = PipeReader.Create(stream, readerOptions);
		PipeWriter writer = PipeWriter.Create(stream, writerOptions);

		return DefaultDuplexPipe.Create(reader, writer);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static IDuplexPipe AsDuplexPipe(
		this Socket socket,
		SocketPipeReaderOptions? readerOptions = null,
		SocketPipeWriterOptions? writerOptions = null)
	{
		PipeReader reader = socket.AsPipeReader(readerOptions);
		PipeWriter writer = socket.AsPipeWriter(writerOptions);

		return DefaultDuplexPipe.Create(reader, writer);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static IDuplexPipe AsDuplexPipe(
		this WebSocket webSocket,
		WebSocketPipeReaderOptions? readerOptions = null,
		WebSocketPipeWriterOptions? writerOptions = null)
	{
		PipeReader reader = webSocket.AsPipeReader(readerOptions);
		PipeWriter writer = webSocket.AsPipeWriter(writerOptions);

		return DefaultDuplexPipe.Create(reader, writer);
	}
}
