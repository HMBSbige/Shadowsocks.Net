using Proxy.Abstractions;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace Socks5;

public sealed partial class Socks5Inbound
{
	private async ValueTask HandleUdpAssociateAsync(
		IDuplexPipe clientPipe,
		IPacketOutbound outbound,
		InboundContext context,
		CancellationToken cancellationToken)
	{
		AddressFamily effectiveFamily = _isWildcardBind
			? context.LocalAddress.AddressFamily
			: _udpRelayBindAddress.AddressFamily;

		if (!TryCreateExpectedUdpSource(effectiveFamily, context.ClientAddress, out SocketAddress expectedUdpSource))
		{
			await Socks5Utils.SendReplyAsync(clientPipe.Output, Socks5Reply.AddressTypeNotSupported, ServerBound.Unspecified, cancellationToken);
			return;
		}

		UdpAssociateSession? session = await TryCreateUdpAssociateSessionAsync(clientPipe.Output, outbound, context.LocalAddress, cancellationToken);

		if (session is null)
		{
			return;
		}

		await using (session)
		{
			LogUdpRelay(session.Bound.Port);
			await Socks5Utils.SendReplyAsync(clientPipe.Output, Socks5Reply.Succeeded, session.Bound, cancellationToken);
			await RunUdpAssociateSessionAsync(clientPipe.Input, session, expectedUdpSource, cancellationToken);
		}
	}

	private async ValueTask<UdpAssociateSession?> TryCreateUdpAssociateSessionAsync(
		PipeWriter replyOutput,
		IPacketOutbound outbound,
		IPAddress tcpLocalAddress,
		CancellationToken cancellationToken)
	{
		IPacketConnection? packetConnection = null;
		Socket? relaySocket = null;

		try
		{
			packetConnection = await outbound.CreatePacketConnectionAsync(cancellationToken);

			// RFC 1928 §6: BND.ADDR/BND.PORT tells the client where to send UDP datagrams.
			// A wildcard address (0.0.0.0 / ::) is unusable as a destination, so fall back
			// to the TCP control connection's local address — an address the client can reach.
			IPAddress bindAddress = _isWildcardBind
				? tcpLocalAddress
				: _udpRelayBindAddress;

			relaySocket = new Socket(bindAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
			relaySocket.Bind(new IPEndPoint(bindAddress, 0));

			if (!TryCreateServerBound(relaySocket.LocalEndPoint?.Serialize(), out ServerBound bound))
			{
				throw new InvalidOperationException("UDP relay socket did not expose a usable LocalEndPoint.");
			}

			return new UdpAssociateSession(packetConnection, relaySocket, bound);
		}
		catch (Exception ex)
		{
			relaySocket?.Dispose();

			if (packetConnection is not null)
			{
				await packetConnection.DisposeAsync();
			}

			await Socks5Utils.SendReplyAsync(replyOutput, MapExceptionToReply(ex), ServerBound.Unspecified, cancellationToken);
			return null;
		}
	}

	private static async ValueTask RunUdpAssociateSessionAsync(
		PipeReader controlInput,
		UdpAssociateSession session,
		SocketAddress expectedUdpSource,
		CancellationToken cancellationToken)
	{
		using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		SocketAddress? lastClientSa = null;
		TaskCompletionSource lastClientSaReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

		Task clientToRemote = Socks5Utils.RelayClientToRemoteAsync(
			session.RelaySocket,
			session.PacketConnection,
			expectedUdpSource,
			senderSa =>
			{
				SocketAddress? current = Volatile.Read(ref lastClientSa);

				if (current is null || !senderSa.Buffer.Span.SequenceEqual(current.Buffer.Span))
				{
					Volatile.Write(ref lastClientSa, Socks5Utils.CloneSocketAddress(senderSa));

					if (current is null)
					{
						lastClientSaReady.TrySetResult();
					}
				}
			},
			linkedCts.Token);
		Task remoteToClient = Socks5Utils.RelayRemoteToClientAsync(
			session.RelaySocket,
			session.PacketConnection,
			lastClientSaReady.Task,
			() => Volatile.Read(ref lastClientSa)!,
			linkedCts.Token);
		Task controlMonitor = Socks5Utils.MonitorControlChannelAsync(controlInput, linkedCts);

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

	private static bool TryCreateExpectedUdpSource(AddressFamily relayFamily, IPAddress clientAddress, out SocketAddress expectedUdpSource)
	{
		if (clientAddress.AddressFamily != relayFamily)
		{
			expectedUdpSource = default!;
			return false;
		}

		expectedUdpSource = new SocketAddress(relayFamily);

		(int addrOffset, _) = Socks5Utils.SockAddrSlice(expectedUdpSource.Family);
		return clientAddress.TryWriteBytes(expectedUdpSource.Buffer.Span.Slice(addrOffset), out _);
	}

	private sealed class UdpAssociateSession(IPacketConnection packetConnection, Socket relaySocket, ServerBound bound) : IAsyncDisposable
	{
		public IPacketConnection PacketConnection { get; } = packetConnection;

		public Socket RelaySocket { get; } = relaySocket;

		public ServerBound Bound { get; } = bound;

		public async ValueTask DisposeAsync()
		{
			RelaySocket.Dispose();
			await PacketConnection.DisposeAsync();
		}
	}
}
