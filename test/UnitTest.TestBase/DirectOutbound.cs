using Pipelines.Extensions;
using Proxy.Abstractions;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UnitTest.TestBase;

/// <summary>
/// Connects directly to the target host via TCP.
/// </summary>
public sealed class DirectOutbound : IStreamOutbound
{
	/// <inheritdoc />
	public async ValueTask<IConnection> ConnectAsync(ProxyDestination destination, CancellationToken cancellationToken)
	{
		Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
		try
		{
			if (IPAddress.TryParse(destination.Host.Span, out IPAddress? ip))
			{
				await socket.ConnectAsync(ip, destination.Port, cancellationToken);
			}
			else
			{
				await socket.ConnectAsync(Encoding.ASCII.GetString(destination.Host.Span), destination.Port, cancellationToken);
			}

			return new SocketConnection(socket);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	private sealed class SocketConnection(Socket socket) : IConnection
	{
		private readonly IDuplexPipe _pipe = socket.AsDuplexPipe();
		public PipeReader Input => _pipe.Input;
		public PipeWriter Output => _pipe.Output;
		public ValueTask DisposeAsync()
		{
			socket.FullClose();
			return ValueTask.CompletedTask;
		}
	}
}
