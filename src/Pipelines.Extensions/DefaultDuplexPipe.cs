using System.IO.Pipelines;

namespace Pipelines.Extensions;

/// <summary>
/// Default implementation of <see cref="IDuplexPipe"/> that wraps a <see cref="PipeReader"/> and <see cref="PipeWriter"/>.
/// </summary>
public sealed class DefaultDuplexPipe : IDuplexPipe
{
	/// <inheritdoc />
	public PipeReader Input { get; }

	/// <inheritdoc />
	public PipeWriter Output { get; }

	/// <summary>
	/// Initializes a new instance of <see cref="DefaultDuplexPipe"/>.
	/// </summary>
	/// <param name="reader">The pipe reader for the input side.</param>
	/// <param name="writer">The pipe writer for the output side.</param>
	public DefaultDuplexPipe(PipeReader reader, PipeWriter writer)
	{
		ArgumentNullException.ThrowIfNull(reader);
		ArgumentNullException.ThrowIfNull(writer);

		Input = reader;
		Output = writer;
	}
}
