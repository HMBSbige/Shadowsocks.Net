using Pipelines.Extensions;
using Proxy.Abstractions;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace UnitTest.TestBase;

public static class TestAcceptLoop
{
	public static async Task RunAsync(TcpListener listener, IStreamInbound inbound, IOutbound outbound, CancellationToken cancellationToken)
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

		static async Task HandleAsync(Socket socket, IStreamInbound inbound, IOutbound outbound, CancellationToken cancellationToken)
		{
			try
			{
				IPEndPoint remote = (IPEndPoint)socket.RemoteEndPoint!;
				IPEndPoint local = (IPEndPoint)socket.LocalEndPoint!;
				InboundContext context = new()
				{
					ClientAddress = remote.Address,
					ClientPort = (ushort)remote.Port,
					LocalAddress = local.Address,
					LocalPort = (ushort)local.Port,
				};
				IDuplexPipe pipe = socket.AsDuplexPipe();
				await inbound.HandleAsync(context, pipe, outbound, cancellationToken);
			}
			finally
			{
				socket.FullClose();
			}
		}
	}
}
