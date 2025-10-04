using System.IO.Pipelines;
using System.Net.Sockets;

namespace Pipelines.Extensions.SocketPipe;

public class SocketPipeReaderOptions
{
	public PipeOptions PipeOptions { get; }

	public SocketFlags SocketFlags { get; }

	public int SizeHint { get; }

	/// <summary>
	/// Default: <see langword="true" />
	/// </summary>
	public bool ShutDownReceive { get; }

	/// <summary>
	/// Default: <see langword="true" />
	/// </summary>
	public bool LeaveOpen { get; }

	internal static readonly SocketPipeReaderOptions Default = new();

	public SocketPipeReaderOptions(
		PipeOptions? pipeOptions = null,
		SocketFlags socketFlags = SocketFlags.None,
		int sizeHint = 0,
		bool shutDownReceive = true,
		bool leaveOpen = true)
	{
		PipeOptions = pipeOptions ?? PipeOptions.Default;
		SocketFlags = socketFlags;
		SizeHint = sizeHint;
		ShutDownReceive = shutDownReceive;
		LeaveOpen = leaveOpen;
	}
}
