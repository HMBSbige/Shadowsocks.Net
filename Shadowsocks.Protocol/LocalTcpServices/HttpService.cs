using HttpProxy;
using Socks5.Models;
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.LocalTcpServices
{
	public class HttpService : ILocalTcpService
	{
		public Socks5CreateOption? Socks5CreateOption { get; set; }

		private readonly HttpToSocks5 _httpToSocks5;

		public HttpService(HttpToSocks5 httpToSocks5)
		{
			_httpToSocks5 = httpToSocks5;
		}

		public bool IsHandle(ReadOnlySequence<byte> buffer)
		{
			return HttpUtils.IsHttpHeader(buffer);
		}

		public async ValueTask HandleAsync(IDuplexPipe pipe, CancellationToken token = default)
		{
			if (Socks5CreateOption is null)
			{
				throw new InvalidOperationException($@"You must set {nameof(Socks5CreateOption)}");
			}

			if (Socks5CreateOption.Address is null)
			{
				throw new InvalidOperationException(@"You must set socks5 address");
			}

			await _httpToSocks5.ForwardToSocks5Async(pipe, Socks5CreateOption, token);
		}
	}
}
