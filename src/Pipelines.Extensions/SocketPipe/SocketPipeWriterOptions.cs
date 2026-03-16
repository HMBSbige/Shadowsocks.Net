using System.Net.Sockets;

namespace Pipelines.Extensions.SocketPipe;

public class SocketPipeWriterOptions(
	SocketFlags socketFlags = SocketFlags.None,
	bool shutDownSend = true,
	bool leaveOpen = true)
{
	public SocketFlags SocketFlags { get; } = socketFlags;

	/// <summary>
	/// Default: <see langword="true" />
	/// </summary>
	public bool ShutDownSend { get; } = shutDownSend;

	/// <summary>
	/// Default: <see langword="true" />
	/// </summary>
	public bool LeaveOpen { get; } = leaveOpen;

	internal static readonly SocketPipeWriterOptions Default = new();
}
