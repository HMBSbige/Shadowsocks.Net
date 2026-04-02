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
			string? host = null;

			if (_logger.IsEnabled(LogLevel.Debug))
			{
				host = Encoding.ASCII.GetString(destination.Host.Span);
				LogConnect(host, destination.Port);
			}

			ConnectionSession? session = await TryCreateConnectSessionAsync(clientPipe.Output, outbound, destination, host, cancellationToken);

			if (session is null)
			{
				return;
			}

			await using (session)
			{
				await Socks5Utils.SendReplyAsync(clientPipe.Output, Socks5Reply.Succeeded, session.Bound, cancellationToken);
				await session.Connection.LinkToAsync(clientPipe, cancellationToken);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(hostBuffer);
		}
	}

	private async ValueTask<ConnectionSession?> TryCreateConnectSessionAsync(
		PipeWriter replyOutput,
		IStreamOutbound outbound,
		ProxyDestination destination,
		string? host,
		CancellationToken cancellationToken)
	{
		try
		{
			IConnection connection = await outbound.ConnectAsync(destination, cancellationToken);

			ServerBound bound = TryCreateServerBound(connection.LocalEndPoint, out ServerBound b) ? b : ServerBound.Unspecified;

			return new ConnectionSession(connection, bound);
		}
		catch (Exception ex)
		{
			if (_logger.IsEnabled(LogLevel.Warning))
			{
				host ??= Encoding.ASCII.GetString(destination.Host.Span);
				LogConnectionFailed(host, destination.Port, ex);
			}

			await Socks5Utils.SendReplyAsync(replyOutput, MapExceptionToReply(ex), ServerBound.Unspecified, cancellationToken);
			return null;
		}
	}

	private sealed class ConnectionSession(IConnection connection, ServerBound bound) : IAsyncDisposable
	{
		public IConnection Connection { get; } = connection;

		public ServerBound Bound { get; } = bound;

		public ValueTask DisposeAsync()
		{
			return Connection.DisposeAsync();
		}
	}
}
