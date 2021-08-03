using Socks5.Enums;
using System;

namespace Socks5.Exceptions
{
	public class MethodUnsupportedException : Exception
	{
		public Method ServerReplyMethod { get; }

		public MethodUnsupportedException(string message, Method serverReplyMethod) : base(message)
		{
			ServerReplyMethod = serverReplyMethod;
		}
	}
}
