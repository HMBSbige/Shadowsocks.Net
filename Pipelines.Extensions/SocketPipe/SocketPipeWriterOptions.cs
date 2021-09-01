
using System.IO.Pipelines;
using System.Net.Sockets;

namespace Pipelines.Extensions.SocketPipe
{
	public class SocketPipeWriterOptions
	{
		public PipeOptions PipeOptions { get; }

		public SocketFlags SocketFlags { get; }

		/// <summary>
		/// Default: <see langword="true" />
		/// </summary>
		public bool ShutDownSend { get; }

		/// <summary>
		/// Default: <see langword="true" />
		/// </summary>
		public bool LeaveOpen { get; }

		internal static readonly SocketPipeWriterOptions Default = new();

		public SocketPipeWriterOptions(
			PipeOptions? pipeOptions = null,
			SocketFlags socketFlags = SocketFlags.None,
			bool shutDownSend = true,
			bool leaveOpen = true)
		{
			PipeOptions = pipeOptions ?? PipeOptions.Default;
			SocketFlags = socketFlags;
			ShutDownSend = shutDownSend;
			LeaveOpen = leaveOpen;
		}
	}
}
