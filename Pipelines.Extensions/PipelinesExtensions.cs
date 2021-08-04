using Nerdbank.Streams;
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pipelines.Extensions
{
	public static class PipelinesExtensions
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
		public static IDuplexPipe AsDuplexPipe(this Stream stream, int sizeHint = 0, PipeOptions? pipeOptions = null, CancellationToken cancellationToken = default)
		{
			//TODO .NET6.0
			return stream.UsePipe(sizeHint, pipeOptions, cancellationToken);
		}

		public static async ValueTask LinkToAsync(this IDuplexPipe pipe1, IDuplexPipe pipe2, CancellationToken token = default)
		{
			var a = pipe1.Input.CopyToAsync(pipe2.Output, token);
			var b = pipe2.Input.CopyToAsync(pipe1.Output, token);

			await Task.WhenAny(a, b); // TODO: CopyToAsync should be fixed in.NET6.0
		}

		public static async ValueTask CopyToAsync(this PipeReader reader,
			PipeWriter target, long size, CancellationToken cancellationToken = default)
		{
			//TODO .NET6.0 ReadAtLeastAsync

			if (size < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(size), @"size must >0.");
			}

			while (true)
			{
				var result = await reader.ReadAsync(cancellationToken);
				var buffer = result.Buffer;

				try
				{
					if (buffer.Length == size)
					{
						await target.WriteAsync(buffer, cancellationToken);
						buffer = buffer.Slice(buffer.Length);
						return;
					}

					if (buffer.Length > size)
					{
						await target.WriteAsync(buffer.Slice(0, size), cancellationToken);
						buffer = buffer.Slice(size);
						reader.CancelPendingRead();
						return;
					}

					await target.WriteAsync(buffer, cancellationToken);
					buffer = buffer.Slice(buffer.Length);
					size -= buffer.Length;

					if (result.IsCompleted)
					{
						throw new InvalidDataException(@"pipe is completed.");
					}
				}
				finally
				{
					reader.AdvanceTo(buffer.Start, buffer.End);
				}
			}
		}
	}
}
