using Microsoft.Extensions.Logging;
using Pipelines.Extensions;
using Proxy.Abstractions;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace Socks5;

public sealed partial class Socks5Inbound
{
	private async ValueTask HandleConnectAsync(
		IDuplexPipe clientPipe,
		IStreamOutbound outbound,
		ServerBound target,
		CancellationToken cancellationToken)
	{
		ProxyDestination destination = Socks5Utils.RentDestination(in target, out byte[] hostBuffer);

		try
		{
			if (_logger.IsEnabled(LogLevel.Debug))
			{
				LogConnect(Encoding.ASCII.GetString(destination.Host.Span), destination.Port);
			}

			(IConnection Connection, ServerBound Bound)? result = await TryCreateConnectSessionAsync(clientPipe.Output, outbound, destination, cancellationToken);

			if (result is null)
			{
				return;
			}

			(IConnection connection, ServerBound bound) = result.Value;

			await using (connection)
			{
				await Socks5Utils.SendReplyAsync(clientPipe.Output, Socks5Reply.Succeeded, bound, cancellationToken);
				await connection.LinkToAsync(clientPipe, cancellationToken);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(hostBuffer);
		}
	}

	private async ValueTask<(IConnection Connection, ServerBound Bound)?> TryCreateConnectSessionAsync(
		PipeWriter replyOutput,
		IStreamOutbound outbound,
		ProxyDestination destination,
		CancellationToken cancellationToken)
	{
		try
		{
			IConnection connection = await outbound.ConnectAsync(destination, cancellationToken);

			if (!TryCreateServerBound(connection.LocalEndPoint, out ServerBound bound))
			{
				bound = ServerBound.Unspecified;
			}

			return (connection, bound);
		}
		catch (Exception ex)
		{
			if (_logger.IsEnabled(LogLevel.Warning))
			{
				LogConnectionFailed(Encoding.ASCII.GetString(destination.Host.Span), destination.Port, ex);
			}

			await Socks5Utils.SendReplyAsync(replyOutput, MapExceptionToReply(ex), ServerBound.Unspecified, cancellationToken);
			return null;
		}
	}
}
