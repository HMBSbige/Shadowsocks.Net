using System.IO.Pipelines;

namespace Pipelines.Extensions;

/// <summary>
/// Default implementation of <see cref="IDuplexPipe"/> that wraps a <see cref="PipeReader"/> and <see cref="PipeWriter"/>.
/// </summary>
public class DefaultDuplexPipe : IDuplexPipe
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

	/// <summary>
	/// Creates a new <see cref="IDuplexPipe"/> from the specified reader and writer.
	/// </summary>
	/// <param name="reader">The pipe reader for the input side.</param>
	/// <param name="writer">The pipe writer for the output side.</param>
	/// <returns>A new <see cref="IDuplexPipe"/> instance.</returns>
	public static IDuplexPipe Create(PipeReader reader, PipeWriter writer)
	{
		return new DefaultDuplexPipe(reader, writer);
	}
}
