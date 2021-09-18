using Pipelines.Extensions;
using Shadowsocks.Protocol.Models;
using Socks5.Utils;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace Shadowsocks.Protocol.TcpClients
{
	public static class ShadowsocksPipeExtensions
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static IDuplexPipe AsShadowsocksPipe(
			this IDuplexPipe pipe,
			ShadowsocksServerInfo serverInfo,
			string targetAddress, ushort targetPort,
			PipeOptions? readerOptions = null,
			PipeOptions? writerOptions = null)
		{
			var reader = pipe.Input.AsShadowsocksPipeReader(serverInfo, readerOptions);
			var writer = pipe.Output.AsShadowsocksPipeWriter(serverInfo, writerOptions);
			writer.WriteShadowsocksHeader(targetAddress, targetPort);

			return DefaultDuplexPipe.Create(reader, writer);
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

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static PipeWriter AsShadowsocksPipeWriter(
			this PipeWriter writer,
			ShadowsocksServerInfo serverInfo,
			PipeOptions? pipeOptions = null)
		{
			return new ShadowsocksPipeWriter(writer, serverInfo, pipeOptions);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static PipeReader AsShadowsocksPipeReader(
			this PipeReader reader,
			ShadowsocksServerInfo serverInfo,
			PipeOptions? pipeOptions = null)
		{
			return new ShadowsocksPipeReader(reader, serverInfo, pipeOptions);
		}
	}
}
