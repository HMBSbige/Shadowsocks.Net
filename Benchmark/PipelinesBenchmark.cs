using BenchmarkDotNet.Attributes;
using Pipelines.Extensions;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Benchmark
{
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
			var t = Task.Run(async () =>
			{
				using var tcpClient = await _server.AcceptTcpClientAsync();
				var reader = tcpClient.GetStream().AsPipeReader();
				var read = 0L;
				while (read < Length)
				{
					var result = await reader.ReadAsync();
					read += result.Buffer.Length;
					reader.AdvanceTo(result.Buffer.End);
				}
			});

			using var client = new TcpClient();
			await client.ConnectAsync(IPAddress.Loopback, _serverPort);

			var writer = client.GetStream().AsPipeWriter();
			for (var i = 0L; i < Length; i += BufferSize)
			{
				await writer.WriteAsync(_buffer);
			}

			await t;
		}

		[Benchmark]
		public async Task SocketAsync()
		{
			var t = Task.Run(async () =>
			{
				using var socket = await _server.AcceptSocketAsync();
				var reader = socket.AsPipeReader();
				var read = 0L;
				while (read < Length)
				{
					var result = await reader.ReadAsync();
					read += result.Buffer.Length;
					reader.AdvanceTo(result.Buffer.End);
				}
			});

			using var client = new TcpClient();
			await client.ConnectAsync(IPAddress.Loopback, _serverPort);

			var writer = client.Client.AsPipeWriter();
			for (var i = 0L; i < Length; i += BufferSize)
			{
				await writer.WriteAsync(_buffer);
			}

			await t;
		}
	}
}
