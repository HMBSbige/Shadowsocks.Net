using HttpProxy;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.LocalTcpServices
{
	public class HttpService : ILocalTcpService
	{
		public IPEndPoint? Socks5EndPoint { get; set; }

		private readonly ILogger<HttpToSocks5> _logger;

		public HttpService(ILogger<HttpToSocks5> logger)
		{
			_logger = logger;
		}

		public bool IsHandle(ReadOnlySequence<byte> buffer)
		{
			return HttpUtils.IsHttpHeader(buffer);
		}

		public async ValueTask HandleAsync(IDuplexPipe pipe, CancellationToken token = default)
		{
			if (Socks5EndPoint is null)
			{
				throw new InvalidOperationException($@"You must set {nameof(Socks5EndPoint)}");
			}

			var http = new HttpToSocks5(_logger);
			await http.ForwardToSocks5Async(pipe, Socks5EndPoint, token);
		}
	}
}
