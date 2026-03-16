namespace Socks5.Exceptions;

public class AuthenticationFailureException(string message, byte statusCode) : Exception(message)
{
	public byte StatusCode { get; } = statusCode;
}
