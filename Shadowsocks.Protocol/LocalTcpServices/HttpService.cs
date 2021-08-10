using HttpProxy;
using Microsoft;
using Socks5.Models;
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
			Verify.Operation(Socks5CreateOption is not null, @"You must set {0}", nameof(Socks5CreateOption));
			Verify.Operation(Socks5CreateOption.Address is not null, @"You must set socks5 address");

			await _httpToSocks5.ForwardToSocks5Async(pipe, Socks5CreateOption, token);
		}
	}
}
