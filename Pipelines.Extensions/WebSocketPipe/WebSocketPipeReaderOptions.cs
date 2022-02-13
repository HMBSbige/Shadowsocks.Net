using System.IO.Pipelines;

namespace Pipelines.Extensions.WebSocketPipe;

public class WebSocketPipeReaderOptions
{
	public PipeOptions PipeOptions { get; }

	public int SizeHint { get; }

	internal static readonly WebSocketPipeReaderOptions Default = new();

	public WebSocketPipeReaderOptions(PipeOptions? pipeOptions = null, int sizeHint = 0)
	{
		PipeOptions = pipeOptions ?? PipeOptions.Default;
		SizeHint = sizeHint;
	}
}
