using Socks5.Enums;

namespace Socks5.Exceptions;

public class MethodUnsupportedException(string message, Method serverReplyMethod) : Exception(message)
{
	public Method ServerReplyMethod { get; } = serverReplyMethod;
}
