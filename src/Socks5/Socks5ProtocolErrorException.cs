namespace Socks5;

/// <summary>
/// Thrown when a SOCKS5 message is invalid, incomplete, or carries a protocol-level error reply.
/// </summary>
/// <param name="message">The descriptive error message.</param>
/// <param name="socks5Reply">The SOCKS5 reply code that best categorizes the protocol failure.</param>
public class Socks5ProtocolErrorException(string message, Socks5Reply socks5Reply) : Exception(message)
{
	/// <summary>
	/// Gets the SOCKS5 reply code that best categorizes the protocol failure.
	/// </summary>
	public Socks5Reply Socks5Reply { get; } = socks5Reply;
}
