using Microsoft;
using Pipelines.Extensions.SocketPipe;
using Pipelines.Extensions.WebSocketPipe;
using System.IO;
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
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async ValueTask LinkToAsync(this IDuplexPipe pipe1, IDuplexPipe pipe2, CancellationToken token = default)
		{
			var a = pipe1.Input.CopyToAsync(pipe2.Output, token);
			var b = pipe2.Input.CopyToAsync(pipe1.Output, token);

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

			var reader = PipeReader.Create(stream, readerOptions);
			var writer = PipeWriter.Create(stream, writerOptions);

			return DefaultDuplexPipe.Create(reader, writer);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static IDuplexPipe AsDuplexPipe(
			this Socket socket,
			SocketPipeReaderOptions? readerOptions = null,
			SocketPipeWriterOptions? writerOptions = null)
		{
			var reader = socket.AsPipeReader(readerOptions);
			var writer = socket.AsPipeWriter(writerOptions);

			return DefaultDuplexPipe.Create(reader, writer);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static IDuplexPipe AsDuplexPipe(
			this WebSocket webSocket,
			WebSocketPipeReaderOptions? readerOptions = null,
			WebSocketPipeWriterOptions? writerOptions = null)
		{
			var reader = webSocket.AsPipeReader(readerOptions);
			var writer = webSocket.AsPipeWriter(writerOptions);

			return DefaultDuplexPipe.Create(reader, writer);
		}
	}
}
