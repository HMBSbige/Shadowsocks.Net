using Pipelines.Extensions;
using Proxy.Abstractions;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace UnitTest.TestBase;

public static class TestAcceptLoop
{
	public static async Task RunAsync(TcpListener listener, IInbound inbound, IOutbound outbound, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				Socket socket = await listener.AcceptSocketAsync(cancellationToken);
				socket.NoDelay = true;
				_ = HandleAsync(socket, inbound, outbound, cancellationToken);
			}
		}
		catch (OperationCanceledException) { }

		return;

		static async Task HandleAsync(Socket socket, IInbound inbound, IOutbound outbound, CancellationToken cancellationToken)
		{
			try
			{
				IDuplexPipe pipe = socket.AsDuplexPipe();
				await inbound.HandleAsync(pipe, outbound, cancellationToken);
			}
			finally
			{
				socket.FullClose();
			}
		}
	}
}
