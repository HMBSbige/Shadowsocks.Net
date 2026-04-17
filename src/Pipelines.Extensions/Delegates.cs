using System.Buffers;

namespace Pipelines.Extensions;

/// <summary>
/// Handles parsing of a <see cref="ReadOnlySequence{T}"/> buffer with caller-supplied state and returns a <see cref="ParseResult"/>.
/// </summary>
/// <typeparam name="TState">Type of the state carried to the callback.</typeparam>
/// <param name="state">State forwarded to the callback.</param>
/// <param name="buffer">The buffer to parse. May be sliced to indicate consumed data.</param>
/// <returns>The result of the parse operation.</returns>
public delegate ParseResult HandleReadOnlySequence<in TState>(TState state, ref ReadOnlySequence<byte> buffer);

/// <summary>
/// Handles parsing of a <see cref="ReadOnlySequence{T}"/> buffer with caller-supplied state, yielding an output value, and returns a <see cref="ParseResult"/>.
/// </summary>
/// <typeparam name="TState">Type of the state carried to the callback.</typeparam>
/// <typeparam name="TOutput">Type of the parsed value produced on success.</typeparam>
/// <param name="state">State forwarded to the callback.</param>
/// <param name="output">Parsed value; must be assigned when <see cref="ParseResult.Success"/> is returned.</param>
/// <param name="buffer">The buffer to parse. May be sliced to indicate consumed data.</param>
/// <returns>The result of the parse operation.</returns>
public delegate ParseResult HandleReadOnlySequence<in TState, TOutput>(TState state, out TOutput output, ref ReadOnlySequence<byte> buffer);

/// <summary>
/// Copies data into the provided <see cref="Span{T}"/> with caller-supplied state and returns the number of bytes written.
/// </summary>
/// <typeparam name="TState">Type of the state carried to the callback.</typeparam>
/// <param name="state">State forwarded to the callback.</param>
/// <param name="buffer">The destination span to write into.</param>
/// <returns>The number of bytes written.</returns>
public delegate int CopyToSpan<in TState>(TState state, Span<byte> buffer);

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
