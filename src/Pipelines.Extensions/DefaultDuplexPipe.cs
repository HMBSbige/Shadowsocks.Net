using System.IO.Pipelines;

namespace Pipelines.Extensions;

public class DefaultDuplexPipe : IDuplexPipe
{
	public PipeReader Input { get; }

	public PipeWriter Output { get; }

	public DefaultDuplexPipe(PipeReader reader, PipeWriter writer)
	{
		ArgumentNullException.ThrowIfNull(reader);
		ArgumentNullException.ThrowIfNull(writer);

		Input = reader;
		Output = writer;
	}

	public static IDuplexPipe Create(PipeReader reader, PipeWriter writer)
	{
		return new DefaultDuplexPipe(reader, writer);
	}
}
