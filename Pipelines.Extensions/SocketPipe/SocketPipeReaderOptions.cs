
using System.IO.Pipelines;
using System.Net.Sockets;

namespace Pipelines.Extensions.SocketPipe
{
	public class SocketPipeReaderOptions
	{
		public PipeOptions PipeOptions { get; }

		public SocketFlags SocketFlags { get; }

		public int SizeHint { get; }

		internal static readonly SocketPipeReaderOptions Default = new();

		public SocketPipeReaderOptions(PipeOptions? pipeOptions = null, SocketFlags socketFlags = SocketFlags.None, int sizeHint = 0)
		{
			PipeOptions = pipeOptions ?? PipeOptions.Default;
			SocketFlags = socketFlags;
			SizeHint = sizeHint;
		}
	}
}
