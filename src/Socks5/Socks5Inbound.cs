using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pipelines.Extensions;
using Proxy.Abstractions;
using Socks5.Protocol;
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

	public static bool IsClientHeader(ReadOnlySequence<byte> buffer)
	{
		SequenceReader<byte> reader = new(buffer);

		if (!reader.TryRead(out byte ver))
		{
			return false;
		}

		if (ver is not Constants.ProtocolVersion)
		{
			return false;
		}

		if (!reader.TryRead(out byte num) || num is 0)
		{
			return false;
		}

		if (reader.Remaining < num)
		{
			return false;
		}

		return true;
	}

	public async ValueTask HandleAsync(InboundContext context, IDuplexPipe clientPipe, IOutbound outbound, CancellationToken cancellationToken = default)
	{
		try
		{
			(Command command, ServerBound target) = await AcceptClientAsync(clientPipe, credential, cancellationToken);

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
						await SendReplyAsync(clientPipe.Output, Socks5Reply.AddressTypeNotSupported, ServerBound.Unspecified, cancellationToken);
						break;
					}

					(int addrOff, _) = SockAddrSlice(expectedSa.Family);
					clientAddr.TryWriteBytes(expectedSa.Buffer.Span.Slice(addrOff), out _);

					await HandleUdpRelayAsync(clientPipe, packet, expectedSa, cancellationToken);
					break;
				}
				default:
					await SendReplyAsync(clientPipe.Output, Socks5Reply.CommandNotSupported, ServerBound.Unspecified, cancellationToken);
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

	private static async ValueTask<(Command command, ServerBound target)> AcceptClientAsync(IDuplexPipe pipe, UserPassAuth? credential, CancellationToken cancellationToken)
	{
		// Cannot use Span<Method> here — local function TryReadClientHandshake captures
		// 'methods' and 'methodCount', and Span cannot be captured by closures.
		Method[] methods = new Method[8];
		int methodCount = 0;

		await pipe.Input.ReadAsync(TryReadClientHandshake, cancellationToken);

		if (methodCount <= 0)
		{
			throw new InvalidDataException(@"Error SOCKS5 header!");
		}

		// Select method
		Method desired = credential?.UserName.Length > 0
			? Method.UsernamePassword
			: Method.NoAuthentication;

		Method method = Method.NoAcceptable;

		for (int i = 0; i < methodCount; i++)
		{
			if (methods[i] == desired)
			{
				method = desired;
				break;
			}
		}

		await pipe.Output.WriteAsync(2, PackMethod, cancellationToken);

		if (method is Method.UsernamePassword && !await UsernamePasswordAuthAsync(pipe, credential, cancellationToken))
		{
			throw new Socks5ProtocolErrorException(@"SOCKS5 auth username password error.", Socks5Reply.ConnectionNotAllowed);
		}

		Command command = default;
		ServerBound target = default;

		if (method is not Method.NoAcceptable)
		{
			(command, target) = await ReadTargetAsync(pipe, cancellationToken);
		}

		return (command, target);

		ParseResult TryReadClientHandshake(ref ReadOnlySequence<byte> buffer)
		{
			return Unpack.ReadClientHandshake(ref buffer, methods, out methodCount) ? ParseResult.Success : ParseResult.NeedsMoreData;
		}

		int PackMethod(Span<byte> span)
		{
			return Pack.Handshake(method, span);
		}
	}

	private static async ValueTask<bool> UsernamePasswordAuthAsync(IDuplexPipe pipe, UserPassAuth? credential, CancellationToken cancellationToken)
	{
		UserPassAuth? clientCredential = null;
		await pipe.Input.ReadAsync(TryReadClientAuth, cancellationToken);

		bool isAuth = clientCredential == credential;

		await pipe.Output.WriteAsync(2, PackReply, cancellationToken);

		return isAuth;

		ParseResult TryReadClientAuth(ref ReadOnlySequence<byte> buffer)
		{
			return Unpack.ReadClientAuth(ref buffer, ref clientCredential) ? ParseResult.Success : ParseResult.NeedsMoreData;
		}

		int PackReply(Span<byte> span)
		{
			return Pack.AuthReply(isAuth, span);
		}
	}

	private static async ValueTask<(Command command, ServerBound target)> ReadTargetAsync(IDuplexPipe pipe, CancellationToken cancellationToken)
	{
		Command command = default;
		ServerBound target = default;
		await pipe.Input.ReadAsync(TryReadCommand, cancellationToken);
		return (command, target);

		ParseResult TryReadCommand(ref ReadOnlySequence<byte> buffer)
		{
			SequenceReader<byte> reader = new(buffer);

			if (!reader.TryRead(out byte ver))
			{
				return ParseResult.NeedsMoreData;
			}

			if (ver is not Constants.ProtocolVersion)
			{
				throw new Socks5ProtocolErrorException($@"client version is not 0x05: 0x{ver:X2}.", Socks5Reply.GeneralFailure);
			}

			if (!reader.TryRead(out byte cmd))
			{
				return ParseResult.NeedsMoreData;
			}

			command = (Command)cmd;

			if (!Enum.IsDefined(command))
			{
				throw new Socks5ProtocolErrorException($@"client sent an unknown command: {command}.", Socks5Reply.CommandNotSupported);
			}

			if (!reader.TryRead(out byte rsv))
			{
				return ParseResult.NeedsMoreData;
			}

			if (rsv is not Constants.Rsv)
			{
				throw new Socks5ProtocolErrorException($@"Protocol failed, RESERVED is not 0x00: 0x{rsv:X2}.", Socks5Reply.GeneralFailure);
			}

			if (!reader.TryRead(out byte type))
			{
				return ParseResult.NeedsMoreData;
			}

			target.Type = (AddressType)type;

			if (!Unpack.ReadDestinationAddress(ref reader, target.Type, target.Host.WriteBuffer, out target.Host.Length))
			{
				return ParseResult.NeedsMoreData;
			}

			if (!reader.TryReadBigEndian(out short port))
			{
				return ParseResult.NeedsMoreData;
			}

			target.Port = (ushort)port;

			buffer = buffer.Slice(reader.Consumed);
			return ParseResult.Success;
		}
	}

	private static async ValueTask SendReplyAsync(PipeWriter output, Socks5Reply reply, ServerBound bound, CancellationToken cancellationToken)
	{
		await output.WriteAsync(Constants.MaxCommandLength, PackCommand, cancellationToken);
		return;

		int PackCommand(Span<byte> span)
		{
			return Pack.ServerReply(reply, bound, span);
		}
	}

	private static ProxyDestination RentDestination(in ServerBound target, out byte[] rentedBuffer)
	{
		rentedBuffer = ArrayPool<byte>.Shared.Rent(target.Host.Length);
		target.Host.Span.CopyTo(rentedBuffer);
		return new ProxyDestination(rentedBuffer.AsMemory(0, target.Host.Length), target.Port);
	}

	private async ValueTask HandleConnectAsync(
		IDuplexPipe clientPipe,
		IStreamOutbound outbound,
		ServerBound target,
		CancellationToken cancellationToken)
	{
		ProxyDestination destination = RentDestination(in target, out byte[] hostBuffer);

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

				await SendReplyAsync(clientPipe.Output, reply, ServerBound.Unspecified, cancellationToken);
				return;
			}

			await using (connection)
			{
				await SendReplyAsync(clientPipe.Output, Socks5Reply.Succeeded, ServerBound.Unspecified, cancellationToken);
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
		CancellationToken cancellationToken)
	{
		await using IPacketConnection packetConnection = await outbound.CreatePacketConnectionAsync(cancellationToken);

		using Socket relaySocket = new(_udpRelayBindAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
		relaySocket.Bind(new IPEndPoint(_udpRelayBindAddress, 0));

		IPEndPoint localEndPoint = (IPEndPoint)relaySocket.LocalEndPoint!;

		ServerBound replyBound = default;
		replyBound.Type = _udpRelayBindAddress.AddressFamily is AddressFamily.InterNetworkV6 ? AddressType.IPv6 : AddressType.IPv4;
		localEndPoint.Address.TryFormat(replyBound.Host.WriteBuffer, out replyBound.Host.Length);
		replyBound.Port = (ushort)localEndPoint.Port;

		LogUdpRelay(localEndPoint.Port);

		await SendReplyAsync(clientPipe.Output, Socks5Reply.Succeeded, replyBound, cancellationToken);

		using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		TaskCompletionSource<SocketAddress> clientSaTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

		Task clientToRemote = RelayClientToRemoteAsync(relaySocket, packetConnection, clientSaTcs, expectedUdpSource, linkedCts.Token);
		Task remoteToClient = RelayRemoteToClientAsync(relaySocket, packetConnection, clientSaTcs, linkedCts.Token);
		Task controlMonitor = MonitorControlChannelAsync(clientPipe.Input, linkedCts);

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

	private static async Task MonitorControlChannelAsync(PipeReader input, CancellationTokenSource linkedCts)
	{
		try
		{
			ReadResult result = await input.ReadAsync(linkedCts.Token);
			input.AdvanceTo(result.Buffer.End);
		}
		catch (OperationCanceledException) { }
		finally
		{
			await linkedCts.CancelAsync();
		}
	}

	/// <summary>
	/// sockaddr layout: port at bytes [2..4], address at IPv4 [4..8] / IPv6 [8..24].
	/// </summary>
	private static (int Offset, int Length) SockAddrSlice(AddressFamily family)
	{
		return family is AddressFamily.InterNetworkV6 ? (8, 16) : (4, 4);
	}

	/// <summary>
	/// RFC 1928 §7: returns true if the sender does NOT match the expected source filter.
	/// Zero address/port bytes mean "any" and always pass.
	/// </summary>
	private static bool IsFilteredOut(SocketAddress expected, SocketAddress sender)
	{
		Span<byte> eBuf = expected.Buffer.Span;
		Span<byte> sBuf = sender.Buffer.Span;

		// Port (big-endian, offset 2): non-zero means must match.
		if ((eBuf[2] | eBuf[3]) is not 0 && (eBuf[2] != sBuf[2] || eBuf[3] != sBuf[3]))
		{
			return true;
		}

		(int addrOffset, int addrLen) = SockAddrSlice(expected.Family);
		Span<byte> eAddr = eBuf.Slice(addrOffset, addrLen);

		return eAddr.ContainsAnyExcept((byte)0) && !eAddr.SequenceEqual(sBuf.Slice(addrOffset, addrLen));
	}

	private static async Task RelayClientToRemoteAsync(
		Socket relaySocket,
		IPacketConnection packetConnection,
		TaskCompletionSource<SocketAddress> clientSaTcs,
		SocketAddress expectedUdpSource,
		CancellationToken cancellationToken)
	{
		byte[] buffer = ArrayPool<byte>.Shared.Rent(0x10000);

		try
		{
			SocketAddress senderSa = new(relaySocket.AddressFamily);

			while (!cancellationToken.IsCancellationRequested)
			{
				int received = await relaySocket.ReceiveFromAsync(
					buffer.AsMemory(),
					SocketFlags.None,
					senderSa,
					cancellationToken);

				if (IsFilteredOut(expectedUdpSource, senderSa))
				{
					continue;
				}

				// Copy on first accepted packet — senderSa is mutated by the next ReceiveFromAsync.
				if (!clientSaTcs.Task.IsCompleted)
				{
					SocketAddress snapshot = new(senderSa.Family);
					senderSa.Buffer.CopyTo(snapshot.Buffer);
					clientSaTcs.TrySetResult(snapshot);
				}

				Socks5UdpReceivePacket packet = Unpack.Udp(buffer.AsMemory(0, received));

				if (packet.Fragment is not 0x00)
				{
					continue;
				}

				byte[] hostBytes = new byte[packet.Host.Length];
				packet.Host.Span.CopyTo(hostBytes);
				ProxyDestination dest = new(hostBytes, packet.Port);

				await packetConnection.SendToAsync(packet.Data, dest, cancellationToken);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	private static async Task RelayRemoteToClientAsync(
		Socket relaySocket,
		IPacketConnection packetConnection,
		TaskCompletionSource<SocketAddress> clientSaTcs,
		CancellationToken cancellationToken)
	{
		SocketAddress clientSa = await clientSaTcs.Task.WaitAsync(cancellationToken);

		byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(0x10000);
		byte[] sendBuffer = ArrayPool<byte>.Shared.Rent(Constants.MaxUdpHandshakeHeaderLength + 0x10000);

		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				PacketReceiveResult result = await packetConnection.ReceiveFromAsync(
					receiveBuffer.AsMemory(),
					cancellationToken);

				int packedLength = Pack.Udp(
					sendBuffer,
					result.RemoteDestination.Host.Span,
					result.RemoteDestination.Port,
					receiveBuffer.AsSpan(0, result.BytesReceived));

				await relaySocket.SendToAsync(
					sendBuffer.AsMemory(0, packedLength),
					SocketFlags.None,
					clientSa,
					cancellationToken);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(receiveBuffer);
			ArrayPool<byte>.Shared.Return(sendBuffer);
		}
	}
}
