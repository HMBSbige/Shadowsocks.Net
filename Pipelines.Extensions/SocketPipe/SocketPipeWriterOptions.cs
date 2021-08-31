
using System.IO.Pipelines;
using System.Net.Sockets;

namespace Pipelines.Extensions.SocketPipe
{
	public class SocketPipeWriterOptions
	{
		public PipeOptions PipeOptions { get; }

		public SocketFlags SocketFlags { get; }

		internal static readonly SocketPipeWriterOptions Default = new();

		public SocketPipeWriterOptions(PipeOptions? pipeOptions = null, SocketFlags socketFlags = SocketFlags.None)
		{
			PipeOptions = pipeOptions ?? PipeOptions.Default;
			SocketFlags = socketFlags;
		}
	}
}
