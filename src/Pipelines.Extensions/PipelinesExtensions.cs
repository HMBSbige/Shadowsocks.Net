using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Pipelines.Extensions;

/// <summary>
/// Extension methods for <see cref="System.IO.Pipelines"/> and related types.
/// </summary>
public static class PipelinesExtensions
{
	extension(PipeReader reader)
	{
		/// <summary>
		/// Reads from the <see cref="PipeReader"/> in a loop, invoking <paramref name="func"/> on each read
		/// until parsing succeeds, fails, or the pipe completes.
		/// </summary>
		/// <param name="func">The delegate that parses the buffer.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
		public async ValueTask<bool> ReadAsync(
			HandleReadOnlySequence func,
			CancellationToken cancellationToken = default)
		{
			while (true)
			{
				ReadResult result = await reader.ReadAsync(cancellationToken);
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

		/// <summary>
		/// Copies exactly <paramref name="size"/> bytes from the <see cref="PipeReader"/> to the <paramref name="target"/>.
		/// </summary>
		/// <param name="target">The destination pipe writer.</param>
		/// <param name="size">The number of bytes to copy.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>The total number of bytes actually copied.</returns>
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

				try
				{
					target.Write(buffer);
					FlushResult flushResult = await target.FlushAsync(cancellationToken);
					flushResult.ThrowIfCanceled(cancellationToken);

					readSize += buffer.Length;
					size -= buffer.Length;

					if (flushResult.IsCompleted || size <= 0 || result.IsCompleted)
					{
						break;
					}
				}
				finally
				{
					reader.AdvanceTo(buffer.End);
				}
			}

			return readSize;
		}

		/// <summary>
		/// Reads from the <see cref="PipeReader"/> and throws if the result is canceled.
		/// </summary>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>The read result.</returns>
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
		/// <summary>
		/// Throws <see cref="OperationCanceledException"/> if the <see cref="ReadResult"/> is canceled.
		/// </summary>
		/// <param name="cancellationToken">The cancellation token to check.</param>
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
		/// <summary>
		/// Writes data to the <see cref="PipeWriter"/> using a <see cref="CopyToSpan"/> delegate.
		/// </summary>
		/// <param name="maxBufferSize">The minimum buffer size to request.</param>
		/// <param name="copyTo">The delegate that writes data into the buffer span.</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(int maxBufferSize, CopyToSpan copyTo)
		{
			Span<byte> memory = writer.GetSpan(maxBufferSize);

			int length = copyTo(memory);

			writer.Advance(length);
		}

		/// <summary>
		/// Writes data to the <see cref="PipeWriter"/> using a <see cref="CopyToSpan"/> delegate and flushes.
		/// </summary>
		/// <param name="maxBufferSize">The minimum buffer size to request.</param>
		/// <param name="copyTo">The delegate that writes data into the buffer span.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>The flush result.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public async ValueTask<FlushResult> WriteAsync(
			int maxBufferSize,
			CopyToSpan copyTo,
			CancellationToken cancellationToken = default)
		{
			writer.Write(maxBufferSize, copyTo);
			return await writer.FlushAsync(cancellationToken);
		}

		/// <summary>
		/// Writes a string to the <see cref="PipeWriter"/> using the specified encoding.
		/// </summary>
		/// <param name="str">The string to write.</param>
		/// <param name="encoding">The encoding to use. Defaults to <see cref="Encoding.UTF8"/>.</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(string str, Encoding? encoding = null)
		{
			encoding ??= Encoding.UTF8;

			Span<byte> span = writer.GetSpan(encoding.GetMaxByteCount(str.Length));
			int length = encoding.GetBytes(str, span);
			writer.Advance(length);
		}

		/// <summary>
		/// Writes a UTF-8 string to the <see cref="PipeWriter"/> and flushes.
		/// </summary>
		/// <param name="str">The string to write.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>The flush result.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public async ValueTask<FlushResult> WriteAsync(string str, CancellationToken cancellationToken = default)
		{
			writer.Write(str);
			return await writer.FlushAsync(cancellationToken);
		}

		/// <summary>
		/// Writes all segments of a <see cref="ReadOnlySequence{T}"/> to the <see cref="PipeWriter"/> without flushing.
		/// </summary>
		/// <param name="sequence">The byte sequence to write.</param>
		public void Write(ReadOnlySequence<byte> sequence)
		{
			foreach (ReadOnlyMemory<byte> memory in sequence)
			{
				writer.Write(memory.Span);
			}
		}

		/// <summary>
		/// Writes a <see cref="ReadOnlySequence{T}"/> to the <see cref="PipeWriter"/> and flushes once.
		/// </summary>
		/// <param name="sequence">The byte sequence to write.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>The flush result.</returns>
		public async ValueTask<FlushResult> WriteAsync(ReadOnlySequence<byte> sequence, CancellationToken cancellationToken = default)
		{
			writer.Write(sequence);
			return await writer.FlushAndCheckIsCanceledAsync(cancellationToken);
		}

		/// <summary>
		/// Flushes the <see cref="PipeWriter"/> and throws if the result is canceled.
		/// </summary>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>The flush result.</returns>
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
		/// <summary>
		/// Throws <see cref="OperationCanceledException"/> if the <see cref="FlushResult"/> is canceled.
		/// </summary>
		/// <param name="cancellationToken">The cancellation token to check.</param>
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
		/// <summary>
		/// Creates a <see cref="PipeReader"/> that wraps the <see cref="Stream"/>.
		/// </summary>
		/// <param name="options">Options for the stream pipe reader.</param>
		/// <returns>A <see cref="PipeReader"/> reading from the stream.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public PipeReader AsPipeReader(StreamPipeReaderOptions? options = null)
		{
			return PipeReader.Create(stream, options);
		}

		/// <summary>
		/// Creates a <see cref="PipeWriter"/> that wraps the <see cref="Stream"/>.
		/// </summary>
		/// <param name="options">Options for the stream pipe writer.</param>
		/// <returns>A <see cref="PipeWriter"/> writing to the stream.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public PipeWriter AsPipeWriter(StreamPipeWriterOptions? options = null)
		{
			return PipeWriter.Create(stream, options);
		}

		/// <summary>
		/// Creates an <see cref="IDuplexPipe"/> that wraps the <see cref="Stream"/> for both reading and writing.
		/// </summary>
		/// <param name="readerOptions">Options for the reader side.</param>
		/// <param name="writerOptions">Options for the writer side.</param>
		/// <returns>A duplex pipe backed by the stream.</returns>
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
		/// <summary>
		/// Wraps the <see cref="ReadOnlySequence{T}"/> as a readable <see cref="Stream"/>.
		/// </summary>
		/// <returns>A read-only stream over the sequence.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Stream AsStream()
		{
			return new ReadOnlySequenceStream(sequence);
		}
	}

	extension(Socket socket)
	{
		/// <summary>
		/// Creates a <see cref="PipeReader"/> backed by the <see cref="Socket"/>.
		/// </summary>
		/// <param name="options">Options for the stream pipe reader.</param>
		/// <returns>A <see cref="PipeReader"/> reading from the socket.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public PipeReader AsPipeReader(StreamPipeReaderOptions? options = null)
		{
			SocketStream stream = new(socket);
			return PipeReader.Create(stream, options);
		}

		/// <summary>
		/// Creates a <see cref="PipeWriter"/> backed by the <see cref="Socket"/>.
		/// </summary>
		/// <param name="options">Options for the stream pipe writer.</param>
		/// <returns>A <see cref="PipeWriter"/> writing to the socket.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public PipeWriter AsPipeWriter(StreamPipeWriterOptions? options = null)
		{
			SocketStream stream = new(socket);
			return PipeWriter.Create(stream, options);
		}

		/// <summary>
		/// Creates an <see cref="IDuplexPipe"/> backed by the <see cref="Socket"/>.
		/// </summary>
		/// <param name="readerOptions">Options for the reader side.</param>
		/// <param name="writerOptions">Options for the writer side.</param>
		/// <returns>A duplex pipe backed by the socket.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public IDuplexPipe AsDuplexPipe(
			StreamPipeReaderOptions? readerOptions = null,
			StreamPipeWriterOptions? writerOptions = null)
		{
			SocketStream stream = new(socket);
			return stream.AsDuplexPipe(readerOptions, writerOptions);
		}

		/// <summary>
		/// Shuts down and disposes the <see cref="Socket"/>, ignoring errors if already disconnected.
		/// </summary>
		public void FullClose()
		{
			try
			{
				socket.Shutdown(SocketShutdown.Both);
			}
			catch (SocketException)
			{
				// already disconnected
			}
			finally
			{
				socket.Dispose();
			}
		}
	}

	extension(WebSocket webSocket)
	{
		/// <summary>
		/// Creates a <see cref="PipeReader"/> backed by the <see cref="WebSocket"/>.
		/// </summary>
		/// <param name="messageType">The WebSocket message type.</param>
		/// <param name="options">Options for the stream pipe reader.</param>
		/// <returns>A <see cref="PipeReader"/> reading from the WebSocket.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public PipeReader AsPipeReader(WebSocketMessageType messageType = WebSocketMessageType.Binary, StreamPipeReaderOptions? options = null)
		{
			WebSocketStream stream = WebSocketStream.Create(webSocket, messageType);
			return PipeReader.Create(stream, options);
		}

		/// <summary>
		/// Creates a <see cref="PipeWriter"/> backed by the <see cref="WebSocket"/>.
		/// </summary>
		/// <param name="messageType">The WebSocket message type.</param>
		/// <param name="options">Options for the stream pipe writer.</param>
		/// <returns>A <see cref="PipeWriter"/> writing to the WebSocket.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public PipeWriter AsPipeWriter(WebSocketMessageType messageType = WebSocketMessageType.Binary, StreamPipeWriterOptions? options = null)
		{
			WebSocketStream stream = WebSocketStream.Create(webSocket, messageType);
			return PipeWriter.Create(stream, options);
		}

		/// <summary>
		/// Creates an <see cref="IDuplexPipe"/> backed by the <see cref="WebSocket"/>.
		/// </summary>
		/// <param name="messageType">The WebSocket message type.</param>
		/// <param name="readerOptions">Options for the reader side.</param>
		/// <param name="writerOptions">Options for the writer side.</param>
		/// <returns>A duplex pipe backed by the WebSocket.</returns>
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
		/// <summary>
		/// Links two <see cref="IDuplexPipe"/> instances by copying data bidirectionally until both complete.
		/// </summary>
		/// <param name="pipe2">The other duplex pipe to link to.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public async ValueTask LinkToAsync(IDuplexPipe pipe2, CancellationToken cancellationToken = default)
		{
			Task a = pipe.Input.CopyToAsync(pipe2.Output, cancellationToken);
			Task b = pipe2.Input.CopyToAsync(pipe.Output, cancellationToken);

			await Task.WhenAll(a, b);
		}

		/// <summary>
		/// Wraps the <see cref="IDuplexPipe"/> as a readable and writable <see cref="Stream"/>.
		/// </summary>
		/// <param name="leaveOpen">If <see langword="true"/>, the pipe is not completed when the stream is disposed.</param>
		/// <returns>A stream backed by the duplex pipe.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Stream AsStream(bool leaveOpen = false)
		{
			return new DuplexPipeStream(pipe, leaveOpen);
		}
	}
}
