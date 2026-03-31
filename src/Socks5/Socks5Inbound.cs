using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pipelines.Extensions;
using Proxy.Abstractions;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

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
				{
					// RFC 1928 §7 MUST: filter by the TCP client's IP address.
					// DST.PORT (MAY) is kept as an additional port filter; zero means "any".
					SocketAddress expectedSa = new(_udpRelayBindAddress.AddressFamily);
					BinaryPrimitives.WriteUInt16BigEndian(expectedSa.Buffer.Span.Slice(2), target.Port);

					IPAddress clientAddr = context.ClientAddress;

					if (clientAddr.AddressFamily != expectedSa.Family)
					{
						await Socks5Utils.SendReplyAsync(clientPipe.Output, Socks5Reply.AddressTypeNotSupported, ServerBound.Unspecified, cancellationToken);
						break;
					}

					(int addrOff, _) = Socks5Utils.SockAddrSlice(expectedSa.Family);
					clientAddr.TryWriteBytes(expectedSa.Buffer.Span.Slice(addrOff), out _);

					await HandleUdpRelayAsync(clientPipe, packet, expectedSa, context.LocalAddress, cancellationToken);
					break;
				}
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

			IConnection connection;

			try
			{
				connection = await outbound.ConnectAsync(destination, cancellationToken);
			}
			catch (Exception ex)
			{
				if (_logger.IsEnabled(LogLevel.Warning))
				{
					host ??= Encoding.ASCII.GetString(destination.Host.Span);
					LogConnectionFailed(host, destination.Port, ex);
				}

				Socks5Reply reply = ex is SocketException socketEx
					? socketEx.SocketErrorCode switch
					{
						SocketError.HostNotFound or SocketError.HostUnreachable => Socks5Reply.HostUnreachable,
						SocketError.ConnectionRefused => Socks5Reply.ConnectionRefused,
						SocketError.NetworkUnreachable => Socks5Reply.NetworkUnreachable,
						_ => Socks5Reply.GeneralFailure,
					}
					: Socks5Reply.GeneralFailure;

				await Socks5Utils.SendReplyAsync(clientPipe.Output, reply, ServerBound.Unspecified, cancellationToken);
				return;
			}

			await using (connection)
			{
				ServerBound bound = connection.LocalEndPoint is { } sa ? ServerBound.FromSocketAddress(sa) : ServerBound.Unspecified;
				await Socks5Utils.SendReplyAsync(clientPipe.Output, Socks5Reply.Succeeded, bound, cancellationToken);
				await connection.LinkToAsync(clientPipe, cancellationToken);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(hostBuffer);
		}
	}

	private async ValueTask HandleUdpRelayAsync(
		IDuplexPipe clientPipe,
		IPacketOutbound outbound,
		SocketAddress expectedUdpSource,
		IPAddress tcpLocalAddress,
		CancellationToken cancellationToken)
	{
		await using IPacketConnection packetConnection = await outbound.CreatePacketConnectionAsync(cancellationToken);

		// RFC 1928 §4: BND.ADDR/BND.PORT tells the client where to send UDP datagrams.
		// A wildcard address (0.0.0.0 / ::) is unusable as a destination, so fall back
		// to the TCP control connection's local address — an address the client can reach.
		IPAddress bindAddress = _udpRelayBindAddress.Equals(IPAddress.Any) || _udpRelayBindAddress.Equals(IPAddress.IPv6Any)
			? tcpLocalAddress
			: _udpRelayBindAddress;

		using Socket relaySocket = new(bindAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
		relaySocket.Bind(new IPEndPoint(bindAddress, 0));

		ServerBound replyBound = relaySocket.LocalEndPoint?.Serialize() is { } sa ? ServerBound.FromSocketAddress(sa) : ServerBound.Unspecified;

		LogUdpRelay(replyBound.Port);

		await Socks5Utils.SendReplyAsync(clientPipe.Output, Socks5Reply.Succeeded, replyBound, cancellationToken);

		using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		TaskCompletionSource<SocketAddress> clientSaTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

		Task clientToRemote = Socks5Utils.RelayClientToRemoteAsync(relaySocket, packetConnection, clientSaTcs, expectedUdpSource, linkedCts.Token);
		Task remoteToClient = Socks5Utils.RelayRemoteToClientAsync(relaySocket, packetConnection, clientSaTcs, linkedCts.Token);
		Task controlMonitor = Socks5Utils.MonitorControlChannelAsync(clientPipe.Input, linkedCts);

		await Task.WhenAny(controlMonitor, Task.WhenAll(clientToRemote, remoteToClient));
		await linkedCts.CancelAsync();

		try
		{
			await clientToRemote;
		}
		catch (OperationCanceledException) { }

		try
		{
			await remoteToClient;
		}
		catch (OperationCanceledException) { }

		try
		{
			await controlMonitor;
		}
		catch (OperationCanceledException) { }
	}
}
