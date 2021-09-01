using System.Net.Sockets;

namespace Pipelines.Extensions
{
	public static class SocketExtensions
	{
		public static void FullClose(this Socket socket)
		{
			try
			{
				socket.Shutdown(SocketShutdown.Both);
			}
			finally
			{
				socket.Dispose();
			}
		}
	}
}
