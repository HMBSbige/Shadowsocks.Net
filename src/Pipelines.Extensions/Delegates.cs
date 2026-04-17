using System.Buffers;

namespace Pipelines.Extensions;

/// <summary>
/// Parses a buffer; returns <see langword="true"/> on success, <see langword="false"/> to request more data.
/// The <paramref name="buffer"/> may be sliced to signal consumed data.
/// </summary>
public delegate bool HandleReadOnlySequence<in TState>(TState state, ref ReadOnlySequence<byte> buffer);

/// <summary>
/// Parses a buffer and yields a value; returns <c>(true, value)</c> on success, <c>(false, default)</c> to request more data.
/// The <paramref name="buffer"/> may be sliced to signal consumed data.
/// </summary>
public delegate (bool Success, TOutput Output) HandleReadOnlySequence<in TState, TOutput>(TState state, ref ReadOnlySequence<byte> buffer);

/// <summary>
/// Writes bytes into <paramref name="span"/> using caller-supplied state and returns the number of bytes written.
/// <typeparamref name="TState"/> may be a <see langword="ref struct"/> (e.g. <see cref="ReadOnlySpan{T}"/>).
/// </summary>
public delegate int SpanWriter<in TState>(TState state, Span<byte> span) where TState : allows ref struct;
