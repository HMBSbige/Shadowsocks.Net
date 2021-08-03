using HttpProxy;
using Microsoft.Extensions.Logging;
using Shadowsocks.Protocol.ServersControllers;
using Shadowsocks.Protocol.TcpClients;
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.LocalTcpServices
{
	public class HttpService : ILocalTcpService
	{
		private readonly ILogger _logger;
		private readonly IServersController _serversController;

		public HttpService(
			ILogger<HttpService> logger,
			IServersController serversController)
		{
			_logger = logger;
			_serversController = serversController;
		}

		public bool IsHandle(ReadOnlySequence<byte> buffer)
		{
			return HttpProxyHandShake.IsHttpHeader(buffer);
		}

		public async ValueTask HandleAsync(IDuplexPipe pipe, CancellationToken token = default)
		{
			var http = new HttpProxyHandShake(pipe);
			if (!await http.TryParseHeaderAsync(token))
			{
				throw new InvalidDataException(@"Error HTTP header!");
			}
#if DEBUG
			_logger.LogDebug(@"Http request handled: {0}:{1}", http.Hostname, http.Port);
			_logger.LogDebug(@"Http request IsConnect: {0}", http.IsConnect);
			_logger.LogDebug(@"Http request IsKeepAlive: {0}", http.IsKeepAlive);
			_logger.LogDebug("Http request Request:\n{0}", http.Request);
#endif
			await using var client = await _serversController.GetServerAsync(http.Hostname!);

			_logger.LogInformation($@"Http relay to {http.Hostname} via {client}");

			var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(http.Request?.Length ?? 0));
			try
			{
				var length = 0;
				if (http.IsConnect)
				{
					await http.SendConnectSuccessAsync(token);
				}
				else
				{
					length = Encoding.UTF8.GetBytes(http.Request, buffer);
				}

				if (client.Pipe is null)
				{
					throw new InvalidOperationException(@"You should TryConnect successfully first!");
				}

				await client.Pipe.Output.SendShadowsocksHeaderAsync(http.Hostname!, http.Port, token);
				await client.Pipe.Output.WriteAsync(buffer.AsMemory(0, length), token);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}

			//if (http.IsConnect || !http.IsKeepAlive)
			{
				await client.Pipe.LinkToAsync(pipe, token);
			}
		}
	}
}
