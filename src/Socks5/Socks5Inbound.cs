using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Proxy.Abstractions;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace Socks5;

public sealed partial class Socks5Inbound(
	UserPassAuth? credential = null,
	ILogger<Socks5Inbound>? logger = null,
	IPAddress? udpRelayBindAddress = null) : IStreamInbound
{
	private readonly ILogger<Socks5Inbound> _logger = logger ?? NullLogger<Socks5Inbound>.Instance;
	private readonly IPAddress _udpRelayBindAddress = udpRelayBindAddress ?? IPAddress.Any;

	[LoggerMessage(Level = LogLevel.Debug, Message = "SOCKS5 CONNECT to {Host}:{Port}")]
	private partial void LogConnect(string host, ushort port);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Connection to {Host}:{Port} failed")]
	private partial void LogConnectionFailed(string host, ushort port, Exception exception);

	[LoggerMessage(Level = LogLevel.Debug, Message = "SOCKS5 UDP ASSOCIATE relay on port {Port}")]
	private partial void LogUdpRelay(int port);

	[LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error handling SOCKS5 request")]
	private partial void LogUnexpectedError(Exception exception);

	public async ValueTask HandleAsync(InboundContext context, IDuplexPipe clientPipe, IOutbound outbound, CancellationToken cancellationToken = default)
	{
		try
		{
			(Command command, ServerBound target) = await Socks5Utils.AcceptClientAsync(clientPipe, credential, cancellationToken);

			switch (command)
			{
				case Command.Connect when outbound is IStreamOutbound stream:
					await HandleConnectAsync(clientPipe, stream, target, cancellationToken);
					break;
				case Command.UdpAssociate when outbound is IPacketOutbound packet:
					await HandleUdpAssociateAsync(clientPipe, packet, target, context, cancellationToken);
					break;
				default:
					await Socks5Utils.SendReplyAsync(clientPipe.Output, Socks5Reply.CommandNotSupported, ServerBound.Unspecified, cancellationToken);
					break;
			}
		}
		catch (Socks5ProtocolErrorException)
		{
			// Protocol-level errors (wrong version, bad auth, etc.) are terminal — close the connection.
		}
		catch (Exception ex)
		{
			LogUnexpectedError(ex);
			throw;
		}
	}

	private static bool TryCreateServerBound(SocketAddress? socketAddress, out ServerBound bound)
	{
		if (socketAddress is null)
		{
			bound = default;
			return false;
		}

		try
		{
			bound = ServerBound.FromSocketAddress(socketAddress);
			return true;
		}
		catch (ArgumentException)
		{
			bound = default;
			return false;
		}
	}

	private static Socks5Reply MapExceptionToReply(Exception ex)
	{
		return ex switch
		{
			Socks5ProtocolErrorException socks5Ex => socks5Ex.Socks5Reply,
			SocketException socketEx => socketEx.SocketErrorCode switch
			{
				SocketError.HostNotFound or SocketError.HostUnreachable => Socks5Reply.HostUnreachable,
				SocketError.ConnectionRefused => Socks5Reply.ConnectionRefused,
				SocketError.NetworkUnreachable => Socks5Reply.NetworkUnreachable,
				_ => Socks5Reply.GeneralFailure,
			},
			_ => Socks5Reply.GeneralFailure,
		};
	}
}
