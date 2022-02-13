namespace Socks5.Exceptions;

public class AuthenticationFailureException : Exception
{
	public byte StatusCode { get; }

	public AuthenticationFailureException(string message, byte statusCode) : base(message)
	{
		StatusCode = statusCode;
	}
}
