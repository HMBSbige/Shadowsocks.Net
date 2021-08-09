using System;
using System.IO;
using System.IO.Pipelines;
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

		public static async ValueTask CopyToAsync(
			this PipeReader reader,
			PipeWriter target,
			long size,
			CancellationToken cancellationToken = default)
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

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void CheckIsCanceled(this ReadResult result, CancellationToken cancellationToken = default)
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
			result.CheckIsCanceled(cancellationToken);
			return result;
		}
	}
}
