using Pipelines.Extensions.SocketPipe;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Pipelines.Extensions;

public static class PipelinesExtensions
{
	extension(PipeReader reader)
	{
		public async ValueTask<bool> ReadAsync(
			HandleReadOnlySequence func,
			CancellationToken token = default)
		{
			while (true)
			{
				ReadResult result = await reader.ReadAsync(token);
				ReadOnlySequence<byte> buffer = result.Buffer;

				try
				{
					ParseResult readResult = func(ref buffer);

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

		public async ValueTask<long> CopyToAsync(
			PipeWriter target,
			long size,
			CancellationToken cancellationToken = default)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(size);

			long readSize = 0L;

			while (true)
			{
				ReadResult result = await reader.ReadAsync(cancellationToken);
				ReadOnlySequence<byte> buffer = result.Buffer;

				if (buffer.Length > size)
				{
					buffer = buffer.Slice(0, size);
				}

				SequencePosition position = buffer.Start;
				SequencePosition consumed = position;

				try
				{
					while (buffer.TryGet(ref position, out ReadOnlyMemory<byte> memory))
					{
						FlushResult flushResult = await target.WriteAsync(memory, cancellationToken);
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
		public async ValueTask<ReadResult> ReadAndCheckIsCanceledAsync(CancellationToken cancellationToken = default)
		{
			ReadResult result = await reader.ReadAsync(cancellationToken);
			result.ThrowIfCanceled(cancellationToken);
			return result;
		}
	}

	extension(ReadResult result)
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void ThrowIfCanceled(CancellationToken cancellationToken = default)
		{
			if (!result.IsCanceled)
			{
				return;
			}

			cancellationToken.ThrowIfCancellationRequested();
			throw new OperationCanceledException(@"The PipeReader was canceled.");
		}
	}

	extension(PipeWriter writer)
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(int maxBufferSize, CopyToSpan copyTo)
		{
			Span<byte> memory = writer.GetSpan(maxBufferSize);

			int length = copyTo(memory);

			writer.Advance(length);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public async ValueTask<FlushResult> WriteAsync(
			int maxBufferSize,
			CopyToSpan copyTo,
			CancellationToken token = default)
		{
			writer.Write(maxBufferSize, copyTo);
			return await writer.FlushAsync(token);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(string str, Encoding? encoding = null)
		{
			encoding ??= Encoding.UTF8;

			Span<byte> span = writer.GetSpan(encoding.GetMaxByteCount(str.Length));
			int length = encoding.GetBytes(str, span);
			writer.Advance(length);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public async ValueTask<FlushResult> WriteAsync(string str, CancellationToken cancellationToken = default)
		{
			writer.Write(str);
			return await writer.FlushAsync(cancellationToken);
		}

		public async ValueTask<FlushResult> WriteAsync(ReadOnlySequence<byte> sequence, CancellationToken cancellationToken = default)
		{
			FlushResult flushResult = default;

			foreach (ReadOnlyMemory<byte> memory in sequence)
			{
				writer.Write(memory.Span);
				flushResult = await writer.FlushAndCheckIsCanceledAsync(cancellationToken);

				if (flushResult.IsCompleted)
				{
					break;
				}
			}

			return flushResult;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public async ValueTask<FlushResult> FlushAndCheckIsCanceledAsync(CancellationToken cancellationToken = default)
		{
			FlushResult flushResult = await writer.FlushAsync(cancellationToken);
			flushResult.ThrowIfCanceled(cancellationToken);
			return flushResult;
		}
	}

	extension(FlushResult flushResult)
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void ThrowIfCanceled(CancellationToken cancellationToken = default)
		{
			if (!flushResult.IsCanceled)
			{
				return;
			}

			cancellationToken.ThrowIfCancellationRequested();
			throw new OperationCanceledException(@"The PipeWriter flush was canceled.");
		}
	}

	extension(Stream stream)
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public PipeReader AsPipeReader(StreamPipeReaderOptions? options = null)
		{
			return PipeReader.Create(stream, options);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public PipeWriter AsPipeWriter(StreamPipeWriterOptions? options = null)
		{
			return PipeWriter.Create(stream, options);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public IDuplexPipe AsDuplexPipe(
			StreamPipeReaderOptions? readerOptions = null,
			StreamPipeWriterOptions? writerOptions = null)
		{
			if (!stream.CanRead)
			{
				throw new InvalidOperationException(@"Stream is not readable.");
			}

			if (!stream.CanWrite)
			{
				throw new InvalidOperationException(@"Stream is not writable.");
			}

			PipeReader reader = PipeReader.Create(stream, readerOptions);
			PipeWriter writer = PipeWriter.Create(stream, writerOptions);

			return DefaultDuplexPipe.Create(reader, writer);
		}
	}

	extension(ReadOnlySequence<byte> sequence)
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Stream AsStream()
		{
			return new ReadOnlySequenceStream(sequence);
		}
	}

	extension(Socket socket)
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public PipeReader AsPipeReader(SocketPipeReaderOptions? options = null)
		{
			return new SocketPipeReader(socket, options ?? SocketPipeReaderOptions.Default);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public PipeWriter AsPipeWriter(SocketPipeWriterOptions? options = null)
		{
			return new SocketPipeWriter(socket, options ?? SocketPipeWriterOptions.Default);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public IDuplexPipe AsDuplexPipe(
			SocketPipeReaderOptions? readerOptions = null,
			SocketPipeWriterOptions? writerOptions = null)
		{
			PipeReader reader = socket.AsPipeReader(readerOptions);
			PipeWriter writer = socket.AsPipeWriter(writerOptions);

			return DefaultDuplexPipe.Create(reader, writer);
		}

		public void FullClose()
		{
			try
			{
				socket.Shutdown(SocketShutdown.Both);
			}
			finally
			{
				socket.Dispose();
			}
		}
	}

	extension(WebSocket webSocket)
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public PipeReader AsPipeReader(WebSocketMessageType messageType = WebSocketMessageType.Binary, StreamPipeReaderOptions? options = null)
		{
			WebSocketStream stream = WebSocketStream.Create(webSocket, messageType);
			return PipeReader.Create(stream, options);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public PipeWriter AsPipeWriter(WebSocketMessageType messageType = WebSocketMessageType.Binary, StreamPipeWriterOptions? options = null)
		{
			WebSocketStream stream = WebSocketStream.Create(webSocket, messageType);
			return PipeWriter.Create(stream, options);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public IDuplexPipe AsDuplexPipe(
			WebSocketMessageType messageType = WebSocketMessageType.Binary,
			StreamPipeReaderOptions? readerOptions = null,
			StreamPipeWriterOptions? writerOptions = null)
		{
			WebSocketStream stream = WebSocketStream.Create(webSocket, messageType);
			return stream.AsDuplexPipe(readerOptions, writerOptions);
		}
	}

	extension(IDuplexPipe pipe)
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public async ValueTask LinkToAsync(IDuplexPipe pipe2, CancellationToken cancellationToken = default)
		{
			Task a = pipe.Input.CopyToAsync(pipe2.Output, cancellationToken);
			Task b = pipe2.Input.CopyToAsync(pipe.Output, cancellationToken);

			await Task.WhenAll(a, b);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Stream AsStream(bool leaveOpen = false)
		{
			return new DuplexPipeStream(pipe, leaveOpen);
		}
	}
}
