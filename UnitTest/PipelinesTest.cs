using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pipelines.Extensions;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace UnitTest;

[TestClass]
public class PipelinesTest
{
	[TestMethod]
	public async Task SocketPipeWriterDoubleFlushTestAsync()
	{
		const long length = 1024 * 1024;
		const long bufferSize = 4096;
		byte[] buffer = new byte[bufferSize];
		RandomNumberGenerator.Fill(buffer);

		TcpListener server = TcpListener.Create(default);
		server.Start();
		try
		{
			Task t = Task.Run(
				async () =>
				{
					using Socket socket = await server.AcceptSocketAsync();
					IDuplexPipe pipe = socket.AsDuplexPipe();
					PipeReader reader = pipe.Input;
					long read = 0L;
					while (read < length)
					{
						ReadResult result = await reader.ReadAsync();
						read += result.Buffer.Length;
						reader.AdvanceTo(result.Buffer.End);
					}
				}
			);

			using TcpClient client = new();
			await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port);

			PipeWriter writer = client.Client.AsDuplexPipe().AsStream().AsPipeWriter();
			for (long i = 0L; i < length; i += bufferSize)
			{
				await writer.WriteAsync(buffer);

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
