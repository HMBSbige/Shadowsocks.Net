namespace Socks5;

/// <summary>
/// Thrown when the SOCKS5 server returns an authentication negotiation result that this client cannot proceed with.
/// </summary>
/// <param name="message">A message that describes why the server's negotiation result cannot be used.</param>
/// <param name="serverReplyMethod">The authentication method or negotiation result value returned by the server.</param>
public class Socks5MethodUnsupportedException(string message, Method serverReplyMethod) : Exception(message)
{
	/// <summary>
	/// Gets the authentication method or negotiation result value returned by the server.
	/// </summary>
	public Method ServerReplyMethod { get; } = serverReplyMethod;
}
