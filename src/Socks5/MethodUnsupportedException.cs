
namespace Socks5;

public class MethodUnsupportedException(string message, Method serverReplyMethod) : Exception(message)
{
	public Method ServerReplyMethod { get; } = serverReplyMethod;
}
