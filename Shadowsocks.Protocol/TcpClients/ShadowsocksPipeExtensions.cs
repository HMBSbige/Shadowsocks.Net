using Shadowsocks.Protocol.Models;
using Socks5.Utils;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.TcpClients
{
	public static class ShadowsocksPipeExtensions
	{
		public static IDuplexPipe AsShadowsocksPipe(
			this IDuplexPipe pipe,
			ShadowsocksServerInfo serverInfo,
			PipeOptions? pipeOptions = null,
			CancellationToken cancellationToken = default)
		{
			return new ShadowsocksDuplexPipe(pipe, serverInfo, pipeOptions, cancellationToken);
		}

		public static async ValueTask<FlushResult> SendShadowsocksHeaderAsync(
			this PipeWriter writer,
			string targetAddress, ushort targetPort, CancellationToken token)
		{
			var memory = writer.GetMemory(1 + 1 + byte.MaxValue + 2);
			var addressLength = Pack.DestinationAddressAndPort(targetAddress, default, targetPort, memory.Span);
			writer.Advance(addressLength);
			return await writer.FlushAsync(token);
		}
	}
}
