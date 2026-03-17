using System.Buffers;

namespace Pipelines.Extensions;

/// <summary>
/// Handles parsing of a <see cref="ReadOnlySequence{T}"/> buffer and returns a <see cref="ParseResult"/>.
/// </summary>
/// <param name="buffer">The buffer to parse. May be sliced to indicate consumed data.</param>
/// <returns>The result of the parse operation.</returns>
public delegate ParseResult HandleReadOnlySequence(ref ReadOnlySequence<byte> buffer);

/// <summary>
/// Copies data into the provided <see cref="Span{T}"/> and returns the number of bytes written.
/// </summary>
/// <param name="buffer">The destination span to write into.</param>
/// <returns>The number of bytes written.</returns>
public delegate int CopyToSpan(Span<byte> buffer);

/// <summary>
/// Represents the result of a parse operation on a pipeline buffer.
/// </summary>
public enum ParseResult
{
	/// <summary>
	/// The parse result is unknown or invalid.
	/// </summary>
	Unknown,

	/// <summary>
	/// More data is needed to complete parsing.
	/// </summary>
	NeedsMoreData,

	/// <summary>
	/// Parsing completed successfully.
	/// </summary>
	Success
}
