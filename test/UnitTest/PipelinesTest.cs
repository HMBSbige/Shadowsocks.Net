using Pipelines.Extensions;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace UnitTest;

public class PipelinesTest
{
	[Test]
	public async Task SocketDoubleFlushTestAsync(CancellationToken cancellationToken)
	{
		const long length = 1024 * 1024;
		const long bufferSize = 4096;
		byte[] buffer = new byte[(int)bufferSize];
		Random.Shared.NextBytes(buffer);

		TcpListener server = TcpListener.Create(default);
		server.Start();

		try
		{
			Task t = Task.Run(async () =>
				{
					using Socket socket = await server.AcceptSocketAsync(cancellationToken);
					IDuplexPipe pipe = socket.AsDuplexPipe();
					PipeReader reader = pipe.Input;
					long read = 0L;

					while (read < length)
					{
						ReadResult result = await reader.ReadAsync(cancellationToken);
						read += result.Buffer.Length;
						reader.AdvanceTo(result.Buffer.End);
					}
				},
				cancellationToken);

			using TcpClient client = new();
			await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port, cancellationToken);

			PipeWriter writer = client.Client.AsDuplexPipe().AsStream().AsPipeWriter();

			for (long i = 0L; i < length; i += bufferSize)
			{
				await writer.WriteAsync(buffer, cancellationToken);

				using CancellationTokenSource cts = new();
				cts.CancelAfter(TimeSpan.FromSeconds(3));

				await writer.FlushAsync(cts.Token);
			}

			await t;
		}
		finally
		{
			server.Stop();
		}
	}

	[Test]
	public async Task SocketStreamDisposeWithOwnsSocketClosesSocketAsync(CancellationToken cancellationToken)
	{
		(TcpListener listener, TcpClient client, Socket serverSocket) = await CreateConnectedSocketPairAsync(cancellationToken);

		try
		{
			SocketStream stream = new(client.Client, ownsSocket: true);
			await stream.DisposeAsync();

			await Assert.That(client.Client.Connected).IsFalse();
		}
		finally
		{
			serverSocket.Dispose();
			client.Dispose();
			listener.Stop();
		}
	}

	[Test]
	public async Task SocketStreamDisposeWithoutOwnsSocketLeavesSocketOpenAsync(CancellationToken cancellationToken)
	{
		(TcpListener listener, TcpClient client, Socket serverSocket) = await CreateConnectedSocketPairAsync(cancellationToken);

		try
		{
			SocketStream stream = new(client.Client);
			await stream.DisposeAsync();

			byte[] payload = [0x01];
			int sent = await client.Client.SendAsync(payload);

			await Assert.That(sent).IsEqualTo(1);
		}
		finally
		{
			serverSocket.Dispose();
			client.Dispose();
			listener.Stop();
		}
	}

	private static async Task<(TcpListener Listener, TcpClient Client, Socket ServerSocket)> CreateConnectedSocketPairAsync(CancellationToken cancellationToken = default)
	{
		TcpListener listener = TcpListener.Create(default);
		listener.Start();

		TcpClient client = new();

		try
		{
			ValueTask<Socket> acceptTask = listener.AcceptSocketAsync(cancellationToken);
			await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
			Socket serverSocket = await acceptTask;
			return (listener, client, serverSocket);
		}
		catch
		{
			client.Dispose();
			listener.Stop();
			throw;
		}
	}
}
