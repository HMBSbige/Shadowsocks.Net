namespace Socks5;

public class AuthenticationFailureException(string message, byte statusCode) : Exception(message)
{
	public byte StatusCode { get; } = statusCode;
}
