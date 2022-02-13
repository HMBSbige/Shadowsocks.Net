using System.IO.Pipelines;

namespace Pipelines.Extensions.WebSocketPipe;

public class WebSocketPipeWriterOptions
{
	public PipeOptions PipeOptions { get; }

	internal static readonly WebSocketPipeWriterOptions Default = new();

	public WebSocketPipeWriterOptions(PipeOptions? pipeOptions = null)
	{
		PipeOptions = pipeOptions ?? PipeOptions.Default;
	}
}
