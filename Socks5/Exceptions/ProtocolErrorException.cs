using Socks5.Enums;

namespace Socks5.Exceptions;

public class ProtocolErrorException : Exception
{
	public Socks5Reply Socks5Reply { get; }

	public ProtocolErrorException(string message) : base(message) { }

	public ProtocolErrorException(string message, Socks5Reply socks5Reply) : base(message)
	{
		Socks5Reply = socks5Reply;
	}
}
