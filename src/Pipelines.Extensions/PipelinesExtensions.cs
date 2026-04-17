using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;

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
		/// <typeparam name="TState">Type of the state passed to <paramref name="func"/>.</typeparam>
		/// <param name="state">State forwarded to <paramref name="func"/>.</param>
		/// <param name="func">The delegate that parses the buffer.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
		public async ValueTask<bool> ReadAsync<TState>(
			TState state,
			HandleReadOnlySequence<TState> func,
			CancellationToken cancellationToken = default)
		{
			while (true)
			{
				ReadResult result = await reader.ReadAndCheckIsCanceledAsync(cancellationToken);
				ReadOnlySequence<byte> buffer = result.Buffer;
				bool success;

				try
				{
					success = func(state, ref buffer);
				}
				catch
				{
					reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
					throw;
				}

				reader.AdvanceTo(buffer.Start, buffer.End);

				if (success)
				{
					return true;
				}

				if (result.IsCompleted)
				{
					return false;
				}
			}
		}

		/// <summary>
		/// Reads from the <see cref="PipeReader"/> in a loop, invoking <paramref name="func"/> on each read
		/// until parsing succeeds (yielding a value), fails, or the pipe completes.
		/// </summary>
		/// <typeparam name="TState">Type of the state passed to <paramref name="func"/>.</typeparam>
		/// <typeparam name="TOutput">Type of the parsed value.</typeparam>
		/// <param name="state">State forwarded to <paramref name="func"/>.</param>
		/// <param name="func">The delegate that parses the buffer and yields the value.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>A tuple of success flag and parsed value (<see langword="default"/> on failure).</returns>
		public async ValueTask<(bool Success, TOutput Output)> ReadAsync<TState, TOutput>(
			TState state,
			HandleReadOnlySequence<TState, TOutput> func,
			CancellationToken cancellationToken = default)
		{
			while (true)
			{
				ReadResult result = await reader.ReadAndCheckIsCanceledAsync(cancellationToken);
				ReadOnlySequence<byte> buffer = result.Buffer;
				bool success;
				TOutput output;

				try
				{
					(success, output) = func(state, ref buffer);
				}
				catch
				{
					reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
					throw;
				}

				reader.AdvanceTo(buffer.Start, buffer.End);

				if (success)
				{
					return (true, output);
				}

				if (result.IsCompleted)
				{
					return (false, default!);
				}
			}
		}

		/// <summary>
		/// Copies up to <paramref name="maxBytes"/> bytes from the <see cref="PipeReader"/> to the <paramref name="target"/>.
		/// </summary>
		/// <param name="target">The destination pipe writer.</param>
		/// <param name="maxBytes">The maximum number of bytes to copy.</param>
		/// <param name="bufferSize">The size of the buffer to use for reading. Defaults to 81920 bytes (80 KB).</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>The actual number of bytes copied, which may be less than <paramref name="maxBytes"/> if the reader completes early.</returns>
		public async ValueTask<long> CopyToAsync(
			PipeWriter target,
			long maxBytes,
			int bufferSize = 81920,
			CancellationToken cancellationToken = default)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

			long totalCopied = 0L;

			while (maxBytes > 0)
			{
				int minimumSize = (int)Math.Min(maxBytes, bufferSize);
				ReadResult result = await reader.ReadAtLeastAsync(minimumSize, cancellationToken);
				result.ThrowIfCanceled(cancellationToken);

				ReadOnlySequence<byte> buffer = result.Buffer.Length <= maxBytes
					? result.Buffer
					: result.Buffer.Slice(0, maxBytes);

				try
				{
					target.Write(buffer);
					FlushResult flushResult = await target.FlushAndCheckIsCanceledAsync(cancellationToken);

					totalCopied += buffer.Length;
					maxBytes -= buffer.Length;

					if (flushResult.IsCompleted || result.IsCompleted)
					{
						break;
					}
				}
				finally
				{
					reader.AdvanceTo(buffer.End);
				}
			}

			return totalCopied;
		}

		/// <summary>
		/// Reads from the <see cref="PipeReader"/> and throws if the result is canceled.
		/// </summary>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>The read result.</returns>
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
		/// Packs bytes into a rented span via <paramref name="write"/> and flushes the writer.
		/// </summary>
		/// <remarks>
		/// Non-async by design: GetSpan/Advance run eagerly so <typeparamref name="TState"/> —
		/// which may be a ref struct — never enters the async state machine.
		/// </remarks>
		public ValueTask<FlushResult> WriteAndFlushAsync<TState>(
			int sizeHint,
			TState state,
			SpanWriter<TState> write,
			CancellationToken cancellationToken = default)
			where TState : allows ref struct
		{
			Span<byte> span = writer.GetSpan(sizeHint);
			int length = write(state, span);
			writer.Advance(length);
			return writer.FlushAndCheckIsCanceledAsync(cancellationToken);
		}

		/// <summary>
		/// Writes all segments of a <see cref="ReadOnlySequence{T}"/> to the <see cref="PipeWriter"/> without flushing.
		/// </summary>
		/// <param name="sequence">The byte sequence to write.</param>
		public void Write(ReadOnlySequence<byte> sequence)
		{
			if (sequence.IsSingleSegment)
			{
				writer.Write(sequence.FirstSpan);
				return;
			}

			WriteMultiSegment(writer, sequence);
			return;

			static void WriteMultiSegment(PipeWriter target, ReadOnlySequence<byte> sequence)
			{
				foreach (ReadOnlyMemory<byte> memory in sequence)
				{
					target.Write(memory.Span);
				}
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
		/// Creates an <see cref="IDuplexPipe"/> that wraps the <see cref="Stream"/> for both reading and writing.
		/// </summary>
		/// <param name="readerOptions">Options for the reader side. Defaults to <c>LeaveOpen = true</c>; the caller owns the stream.</param>
		/// <param name="writerOptions">Options for the writer side. Defaults to <c>LeaveOpen = true</c>; the caller owns the stream.</param>
		/// <returns>A duplex pipe backed by the stream.</returns>
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

			readerOptions ??= new StreamPipeReaderOptions(leaveOpen: true);
			writerOptions ??= new StreamPipeWriterOptions(leaveOpen: true);

			PipeReader reader = PipeReader.Create(stream, readerOptions);
			PipeWriter writer = PipeWriter.Create(stream, writerOptions);

			return new DefaultDuplexPipe(reader, writer);
		}
	}

	extension(ReadOnlySequence<byte> sequence)
	{
		/// <summary>
		/// Wraps the <see cref="ReadOnlySequence{T}"/> as a readable <see cref="Stream"/>.
		/// </summary>
		/// <returns>A read-only stream over the sequence.</returns>
		public Stream AsStream()
		{
			return new ReadOnlySequenceStream(sequence);
		}
	}

	extension(Socket socket)
	{
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

	extension(IDuplexPipe pipe)
	{
		/// <summary>
		/// Bridges two <see cref="IDuplexPipe"/> instances by copying data bidirectionally until both sides complete.
		/// </summary>
		/// <param name="pipe2">The other duplex pipe to bridge with.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		public Task BridgeAsync(IDuplexPipe pipe2, CancellationToken cancellationToken = default)
		{
			return Task.WhenAll(
				pipe.Input.CopyToAsync(pipe2.Output, cancellationToken),
				pipe2.Input.CopyToAsync(pipe.Output, cancellationToken));
		}

		/// <summary>
		/// Wraps the <see cref="IDuplexPipe"/> as a readable and writable <see cref="Stream"/>.
		/// </summary>
		/// <param name="leaveOpen">If <see langword="true"/>, the pipe is not completed when the stream is disposed.</param>
		/// <returns>A stream backed by the duplex pipe.</returns>
		public Stream AsStream(bool leaveOpen = false)
		{
			return new DuplexPipeStream(pipe, leaveOpen);
		}
	}
}
