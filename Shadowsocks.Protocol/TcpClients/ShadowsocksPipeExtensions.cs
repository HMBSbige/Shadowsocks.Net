using Shadowsocks.Protocol.Models;
using Socks5.Utils;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Shadowsocks.Protocol.TcpClients
{
	public static class ShadowsocksPipeExtensions
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static IDuplexPipe AsShadowsocksPipe(
			this IDuplexPipe pipe,
			ShadowsocksServerInfo serverInfo,
			string targetAddress, ushort targetPort,
			PipeOptions? pipeOptions = null,
			CancellationToken cancellationToken = default)
		{
			return new ShadowsocksDuplexPipe(pipe, serverInfo, targetAddress, targetPort, pipeOptions, cancellationToken);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteShadowsocksHeader(
			this PipeWriter writer,
			string targetAddress, ushort targetPort)
		{
			var span = writer.GetSpan(1 + 1 + byte.MaxValue + 2);
			var addressLength = Pack.DestinationAddressAndPort(targetAddress, default, targetPort, span);
			writer.Advance(addressLength);
		}
	}
}
