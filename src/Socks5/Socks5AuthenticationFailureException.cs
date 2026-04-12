namespace Socks5;

/// <summary>
/// Thrown when the SOCKS5 proxy replies with an authentication failure status.
/// </summary>
/// <param name="message">The error message.</param>
/// <param name="statusCode">The authentication status byte returned by the server.</param>
public class Socks5AuthenticationFailureException(string message, byte statusCode) : Exception(message)
{
	/// <summary>
	/// Gets the raw authentication status byte returned by the server.
	/// </summary>
	public byte StatusCode { get; } = statusCode;
}
