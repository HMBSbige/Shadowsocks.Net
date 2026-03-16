using Socks5.Enums;

namespace Socks5.Exceptions;

public class Socks5ProtocolErrorException(string message, Socks5Reply socks5Reply) : Exception(message)
{
	public Socks5Reply Socks5Reply { get; } = socks5Reply;
}
