using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Socks5;

/// <summary>
/// A fixed-size, inline host buffer with tracked length. Zero-allocation.
/// </summary>
internal struct HostField
{
	private const int MaxHostLength = 255;

	private Buffer _buffer;
	public int Length;

	[UnscopedRef]
	public readonly ReadOnlySpan<byte> Span => ((ReadOnlySpan<byte>)_buffer).Slice(0, Length);

	/// <summary>
	/// Set <see cref="Length"/> after writing.
	/// </summary>
	[UnscopedRef]
	public Span<byte> WriteBuffer => _buffer;

	[InlineArray(MaxHostLength)]
	private struct Buffer
	{
		private byte _element;
	}
}
