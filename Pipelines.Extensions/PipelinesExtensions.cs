using Microsoft;
using System.IO;
using System.IO.Pipelines;
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

			var task = await Task.WhenAny(a, b);
			if (task.IsCompletedSuccessfully)
			{
				await Task.WhenAll(a, b);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static IDuplexPipe AsDuplexPipe(this Stream stream,
			StreamPipeReaderOptions? readerOptions = null,
			StreamPipeWriterOptions? writerOptions = null)
		{
			Requires.Argument(stream.CanRead, nameof(stream), @"Stream is not readable.");
			Requires.Argument(stream.CanWrite, nameof(stream), @"Stream is not writable.");

			var reader = PipeReader.Create(stream, readerOptions);
			var writer = PipeWriter.Create(stream, writerOptions);

			return DefaultDuplexPipe.Create(reader, writer);
		}
	}
}
