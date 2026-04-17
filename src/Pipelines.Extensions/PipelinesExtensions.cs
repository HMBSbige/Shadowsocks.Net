using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
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

				try
				{
					ParseResult readResult = func(state, ref buffer);

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

				try
				{
					ParseResult readResult = func(state, out TOutput output, ref buffer);

					if (readResult is ParseResult.Success)
					{
						return (true, output);
					}

					if (readResult is not ParseResult.NeedsMoreData || result.IsCompleted)
					{
						return (false, default!);
					}
				}
				finally
				{
					reader.AdvanceTo(buffer.Start, buffer.End);
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
		/// Writes data to the <see cref="PipeWriter"/> using a <see cref="CopyToSpan{TState}"/> delegate with caller-supplied state.
		/// </summary>
		/// <typeparam name="TState">Type of the state passed to <paramref name="copyTo"/>.</typeparam>
		/// <param name="maxBufferSize">The minimum buffer size to request.</param>
		/// <param name="state">State forwarded to <paramref name="copyTo"/>.</param>
		/// <param name="copyTo">The delegate that writes data into the buffer span.</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write<TState>(int maxBufferSize, TState state, CopyToSpan<TState> copyTo)
		{
			Span<byte> memory = writer.GetSpan(maxBufferSize);
			int length = copyTo(state, memory);
			writer.Advance(length);
		}

		/// <summary>
		/// Writes data to the <see cref="PipeWriter"/> using a <see cref="CopyToSpan{TState}"/> delegate with caller-supplied state and flushes.
		/// </summary>
		/// <typeparam name="TState">Type of the state passed to <paramref name="copyTo"/>.</typeparam>
		/// <param name="maxBufferSize">The minimum buffer size to request.</param>
		/// <param name="state">State forwarded to <paramref name="copyTo"/>.</param>
		/// <param name="copyTo">The delegate that writes data into the buffer span.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <returns>The flush result.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public async ValueTask<FlushResult> WriteAsync<TState>(
			int maxBufferSize,
			TState state,
			CopyToSpan<TState> copyTo,
			CancellationToken cancellationToken = default)
		{
			writer.Write(maxBufferSize, state, copyTo);
			return await writer.FlushAndCheckIsCanceledAsync(cancellationToken);
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
		/// <param name="readerOptions">Options for the reader side. Defaults to <c>LeaveOpen = true</c>; the caller owns the stream.</param>
		/// <param name="writerOptions">Options for the writer side. Defaults to <c>LeaveOpen = true</c>; the caller owns the stream.</param>
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

			readerOptions ??= new StreamPipeReaderOptions(leaveOpen: true);
			writerOptions ??= new StreamPipeWriterOptions(leaveOpen: true);

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
