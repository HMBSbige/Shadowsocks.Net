using Socks5.Enums;

namespace Socks5.Exceptions;

public class Socks5ProtocolErrorException : Exception
{
	public Socks5Reply Socks5Reply { get; }

	public Socks5ProtocolErrorException(string message, Socks5Reply socks5Reply) : base(message)
	{
		Socks5Reply = socks5Reply;
	}
}
