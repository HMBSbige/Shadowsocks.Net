using Socks5.Protocol;
using System.Buffers;

namespace Socks5;

/// <summary>
/// Provides helper methods for working with SOCKS5 protocol messages.
/// </summary>
public static partial class Socks5Utils
{
	/// <summary>
	/// Determines whether a buffer starts with a syntactically valid SOCKS5 client greeting header.
	/// </summary>
	/// <param name="buffer">The buffer to inspect.</param>
	/// <returns><see langword="true"/> when the buffer contains a complete SOCKS5 greeting header prefix; otherwise, <see langword="false"/>.</returns>
	public static bool IsSocks5Header(this ReadOnlySequence<byte> buffer)
	{
		SequenceReader<byte> reader = new(buffer);

		return reader.TryRead(out byte ver)
			&& ver is Constants.ProtocolVersion
			&& reader.TryRead(out byte num)
			&& num is not 0
			&& reader.Remaining >= num;
	}
}
