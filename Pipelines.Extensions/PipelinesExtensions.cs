using Nerdbank.Streams;
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
		public static IDuplexPipe AsDuplexPipe(this Stream stream, int sizeHint = 0, PipeOptions? pipeOptions = null, CancellationToken cancellationToken = default)
		{
			//TODO .NET6.0
			return stream.UsePipe(sizeHint, pipeOptions, cancellationToken);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async ValueTask LinkToAsync(this IDuplexPipe pipe1, IDuplexPipe pipe2, CancellationToken token = default)
		{
			var a = pipe1.Input.CopyToAsync(pipe2.Output, token);
			var b = pipe2.Input.CopyToAsync(pipe1.Output, token);

			await Task.WhenAny(a, b); // TODO: CopyToAsync should be fixed in.NET6.0
		}
	}
}
