using Pipelines.Extensions;
using Pipelines.Extensions.SocketPipe;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace UnitTest;

public class PipelinesTest
{
	[Test]
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
			Task t = Task.Run(async () =>
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

	[Test]
	public async Task SocketPipeWriterCancelPendingFlushReturnsCanceledResultAsync()
	{
		(TcpListener listener, TcpClient client, Socket serverSocket) = await CreateConnectedSocketPairAsync();

		try
		{
			PipeWriter writer = client.Client.AsPipeWriter();
			writer.CancelPendingFlush();

			FlushResult result = await writer.FlushAsync();
			FlushResult next = await writer.FlushAsync();

			await Assert.That(result.IsCanceled).IsTrue();
			await Assert.That(result.IsCompleted).IsFalse();
			await Assert.That(next.IsCanceled).IsFalse();
		}
		finally
		{
			serverSocket.Dispose();
			client.Dispose();
			listener.Stop();
		}
	}

	[Test]
	public async Task SocketPipeWriterEmptyFlushObservesCanceledTokenAsync()
	{
		(TcpListener listener, TcpClient client, Socket serverSocket) = await CreateConnectedSocketPairAsync();

		try
		{
			PipeWriter writer = client.Client.AsPipeWriter();
			using CancellationTokenSource cts = new();
			await cts.CancelAsync();

			bool canceled = false;

			try
			{
				await writer.FlushAsync(cts.Token);
			}
			catch (TaskCanceledException)
			{
				canceled = true;
			}

			await Assert.That(canceled).IsTrue();
		}
		finally
		{
			serverSocket.Dispose();
			client.Dispose();
			listener.Stop();
		}
	}

	[Test]
	public async Task SocketPipeWriterCanceledTokenTakesPrecedenceOverPendingFlushCancellationAsync()
	{
		(TcpListener listener, TcpClient client, Socket serverSocket) = await CreateConnectedSocketPairAsync();

		try
		{
			PipeWriter writer = client.Client.AsPipeWriter();
			writer.CancelPendingFlush();

			using CancellationTokenSource cts = new();
			await cts.CancelAsync();

			bool canceled = false;

			try
			{
				await writer.FlushAsync(cts.Token);
			}
			catch (TaskCanceledException)
			{
				canceled = true;
			}

			await Assert.That(canceled).IsTrue();
		}
		finally
		{
			serverSocket.Dispose();
			client.Dispose();
			listener.Stop();
		}
	}

	[Test]
	public async Task SocketPipeWriterCanceledFlushPreservesBufferedDataAsync()
	{
		(TcpListener listener, TcpClient client, Socket serverSocket) = await CreateConnectedSocketPairAsync();

		try
		{
			byte[] payload = RandomNumberGenerator.GetBytes(64 * 1024);
			PipeWriter writer = client.Client.AsPipeWriter();

			WriteUnflushed(writer, payload);
			await Assert.That(writer.UnflushedBytes).IsEqualTo(payload.Length);

			writer.CancelPendingFlush();

			FlushResult canceled = await writer.FlushAsync();
			await Assert.That(canceled.IsCanceled).IsTrue();
			await Assert.That(writer.UnflushedBytes).IsEqualTo(payload.Length);

			FlushResult flushed = await writer.FlushAsync();
			byte[] received = await ReceiveExactAsync(serverSocket, payload.Length);

			await Assert.That(flushed.IsCanceled).IsFalse();
			await Assert.That(writer.UnflushedBytes).IsEqualTo(0);
			await Assert.That(received.SequenceEqual(payload)).IsTrue();
		}
		finally
		{
			serverSocket.Dispose();
			client.Dispose();
			listener.Stop();
		}
	}

	[Test]
	public async Task SocketPipeWriterCanceledTokenPreservesBufferedDataAsync()
	{
		(TcpListener listener, TcpClient client, Socket serverSocket) = await CreateConnectedSocketPairAsync();

		try
		{
			byte[] payload = RandomNumberGenerator.GetBytes(64 * 1024);
			PipeWriter writer = client.Client.AsPipeWriter();

			WriteUnflushed(writer, payload);
			await Assert.That(writer.UnflushedBytes).IsEqualTo(payload.Length);

			using CancellationTokenSource cts = new();
			await cts.CancelAsync();

			bool canceled = false;

			try
			{
				await writer.FlushAsync(cts.Token);
			}
			catch (TaskCanceledException)
			{
				canceled = true;
			}

			await Assert.That(canceled).IsTrue();
			await Assert.That(writer.UnflushedBytes).IsEqualTo(payload.Length);

			FlushResult flushed = await writer.FlushAsync(CancellationToken.None);
			byte[] received = await ReceiveExactAsync(serverSocket, payload.Length);

			await Assert.That(flushed.IsCanceled).IsFalse();
			await Assert.That(writer.UnflushedBytes).IsEqualTo(0);
			await Assert.That(received.SequenceEqual(payload)).IsTrue();
		}
		finally
		{
			serverSocket.Dispose();
			client.Dispose();
			listener.Stop();
		}
	}

	[Test]
	public async Task SocketPipeWriterWriteAfterCompleteThrowsInvalidOperationExceptionAsync()
	{
		(TcpListener listener, TcpClient client, Socket serverSocket) = await CreateConnectedSocketPairAsync();

		try
		{
			PipeWriter writer = client.Client.AsPipeWriter(new SocketPipeWriterOptions(shutDownSend: false, leaveOpen: true));
			await writer.CompleteAsync();

			await Assert.That(() => WriteUnflushed(writer, [0x2A])).ThrowsExactly<InvalidOperationException>();
		}
		finally
		{
			serverSocket.Dispose();
			client.Dispose();
			listener.Stop();
		}
	}

	[Test]
	public async Task SocketPipeReaderFirstTryReadReturnsBufferedDataAsync()
	{
		(TcpListener listener, TcpClient client, Socket serverSocket) = await CreateConnectedSocketPairAsync();
		PipeReader? reader = null;

		try
		{
			byte[] payload = RandomNumberGenerator.GetBytes(32);
			reader = client.Client.AsPipeReader();

			int sent = await serverSocket.SendAsync(payload);
			await Assert.That(sent).IsEqualTo(payload.Length);

			// Give the background receive loop time to move socket data into the pipe
			// before the very first TryRead() call.
			await Task.Delay(200);

			bool read = reader.TryRead(out ReadResult result);

			await Assert.That(read).IsTrue();

			if (read)
			{
				byte[] actual = new byte[result.Buffer.Length];
				int offset = 0;

				foreach (ReadOnlyMemory<byte> memory in result.Buffer)
				{
					memory.CopyTo(actual.AsMemory(offset));
					offset += memory.Length;
				}

				reader.AdvanceTo(result.Buffer.End);
				await Assert.That(actual.SequenceEqual(payload)).IsTrue();
			}
		}
		finally
		{
			if (reader is not null)
			{
				await reader.CompleteAsync();
			}

			serverSocket.Dispose();
			client.Dispose();
			listener.Stop();
		}
	}

	private static async Task<(TcpListener Listener, TcpClient Client, Socket ServerSocket)> CreateConnectedSocketPairAsync()
	{
		TcpListener listener = TcpListener.Create(default);
		listener.Start();

		TcpClient client = new();

		try
		{
			Task<Socket> acceptTask = listener.AcceptSocketAsync();
			await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
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

	private static void WriteUnflushed(PipeWriter writer, ReadOnlySpan<byte> buffer)
	{
		buffer.CopyTo(writer.GetSpan(buffer.Length));
		writer.Advance(buffer.Length);
	}

	private static async Task<byte[]> ReceiveExactAsync(Socket socket, int length)
	{
		byte[] buffer = new byte[length];
		int received = 0;

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

		while (received < length)
		{
			int read = await socket.ReceiveAsync(buffer.AsMemory(received), SocketFlags.None, cts.Token);

			if (read == 0)
			{
				throw new EndOfStreamException(@"Socket closed before receiving the expected number of bytes.");
			}

			received += read;
		}

		return buffer;
	}
}
