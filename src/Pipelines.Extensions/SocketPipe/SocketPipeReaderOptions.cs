using System.IO.Pipelines;
using System.Net.Sockets;

namespace Pipelines.Extensions.SocketPipe;

public class SocketPipeReaderOptions(
	PipeOptions? pipeOptions = null,
	SocketFlags socketFlags = SocketFlags.None,
	int sizeHint = 0,
	bool shutDownReceive = true,
	bool leaveOpen = true)
{
	public PipeOptions PipeOptions { get; } = pipeOptions ?? PipeOptions.Default;

	public SocketFlags SocketFlags { get; } = socketFlags;

	public int SizeHint { get; } = sizeHint;

	/// <summary>
	/// Default: <see langword="true" />
	/// </summary>
	public bool ShutDownReceive { get; } = shutDownReceive;

	/// <summary>
	/// Default: <see langword="true" />
	/// </summary>
	public bool LeaveOpen { get; } = leaveOpen;

	internal static readonly SocketPipeReaderOptions Default = new();
}
