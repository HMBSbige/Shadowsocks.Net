using BenchmarkDotNet.Attributes;
using Pipelines.Extensions;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Benchmark;

[MemoryDiagnoser, RankColumn]
public class PipelinesBenchmark
{
	[Params(100 * 1024 * 1024)]
	public long Length { get; set; }

	private readonly TcpListener _server = TcpListener.Create(default);
	private int _serverPort;
	private const int BufferSize = 4096;
	private readonly byte[] _buffer = new byte[BufferSize];

	[GlobalSetup]
	public void Setup()
	{
		_server.Start();
		_serverPort = ((IPEndPoint)_server.LocalEndpoint).Port;
		RandomNumberGenerator.Fill(_buffer);
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		_server.Stop();
	}

	[Benchmark(Baseline = true)]
	public async Task StreamAsync()
	{
		Task t = Task.Run(
			async () =>
			{
				using TcpClient tcpClient = await _server.AcceptTcpClientAsync();
				PipeReader reader = tcpClient.GetStream().AsPipeReader();
				long read = 0L;
				while (read < Length)
				{
					ReadResult result = await reader.ReadAsync();
					read += result.Buffer.Length;
					reader.AdvanceTo(result.Buffer.End);
				}
			}
		);

		using TcpClient client = new();
		await client.ConnectAsync(IPAddress.Loopback, _serverPort);

		PipeWriter writer = client.GetStream().AsPipeWriter();
		for (long i = 0L; i < Length; i += BufferSize)
		{
			await writer.WriteAsync(_buffer);
		}

		await t;
	}

	[Benchmark]
	public async Task SocketAsync()
	{
		Task t = Task.Run(
			async () =>
			{
				using Socket socket = await _server.AcceptSocketAsync();
				PipeReader reader = socket.AsPipeReader();
				long read = 0L;
				while (read < Length)
				{
					ReadResult result = await reader.ReadAsync();
					read += result.Buffer.Length;
					reader.AdvanceTo(result.Buffer.End);
				}
			}
		);

		using TcpClient client = new();
		await client.ConnectAsync(IPAddress.Loopback, _serverPort);

		PipeWriter writer = client.Client.AsPipeWriter();
		for (long i = 0L; i < Length; i += BufferSize)
		{
			await writer.WriteAsync(_buffer);
		}

		await t;
	}
}
