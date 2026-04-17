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
					await using NetworkStream stream = new(socket);
					IDuplexPipe pipe = stream.AsDuplexPipe();
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

			await using NetworkStream clientStream = new(client.Client);
			PipeWriter writer = PipeWriter.Create(clientStream.AsDuplexPipe().AsStream());

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
}
