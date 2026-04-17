using Pipelines.Extensions;
using Proxy.Abstractions;
using Socks5.Protocol;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace Socks5;

public static partial class Socks5Utils
{
	internal static async ValueTask<(Command command, ServerBound target)> AcceptClientAsync(IDuplexPipe pipe, UserPassAuth? credential, CancellationToken cancellationToken)
	{
		Method desired = credential is not null
			? Method.UsernamePassword
			: Method.NoAuthentication;

		(bool greetingOk, Method method) = await pipe.Input.ReadAsync(
			desired,
			static (desired, ref buf) =>
			{
				bool ok = Unpack.ReadClientHandshake(ref buf, desired, out Method m);
				return (ok, m);
			},
			cancellationToken);

		if (!greetingOk)
		{
			throw new InvalidDataException(@"Incomplete SOCKS5 greeting.");
		}

		await pipe.Output.WriteAndFlushAsync(
			2,
			method,
			Pack.Handshake,
			cancellationToken);

		if (method is Method.NoAcceptable)
		{
			throw new Socks5ProtocolErrorException("No acceptable authentication method (RFC 1928 §3).", Socks5Reply.ConnectionNotAllowed);
		}

		if (method is Method.UsernamePassword)
		{
			if (credential is not { } expectedCredential)
			{
				throw new InvalidOperationException("Username/password auth requires configured credentials.");
			}

			if (!await UsernamePasswordAuthAsync(pipe, expectedCredential, cancellationToken))
			{
				throw new Socks5ProtocolErrorException(@"SOCKS5 auth username password error.", Socks5Reply.ConnectionNotAllowed);
			}
		}

		try
		{
			return await ReadTargetAsync(pipe, cancellationToken);
		}
		catch (Socks5ProtocolErrorException ex) when (ex.Socks5Reply is Socks5Reply.AddressTypeNotSupported)
		{
			await SendReplyAsync(pipe.Output, ex.Socks5Reply, ServerBound.Unspecified, cancellationToken);
			throw;
		}
	}

	private static async ValueTask<bool> UsernamePasswordAuthAsync(IDuplexPipe pipe, UserPassAuth credential, CancellationToken cancellationToken)
	{
		(bool ok, bool isAuth) = await pipe.Input.ReadAsync(
			credential,
			static (credential, ref buf) =>
			{
				bool ok = Unpack.ReadClientAuth(ref buf, credential, out bool auth);
				return (ok, auth);
			},
			cancellationToken);

		if (!ok)
		{
			throw new Socks5ProtocolErrorException(@"Incomplete SOCKS5 auth request.", Socks5Reply.GeneralFailure);
		}

		await pipe.Output.WriteAndFlushAsync(
			2,
			isAuth,
			Pack.AuthReply,
			cancellationToken);

		return isAuth;
	}

	private static async ValueTask<(Command command, ServerBound target)> ReadTargetAsync(IDuplexPipe pipe, CancellationToken cancellationToken)
	{
		(bool ok, (Command command, ServerBound target) result) = await pipe.Input.ReadAsync<byte, (Command, ServerBound)>(
			0,
			static (_, ref buf) =>
			{
				bool ok = Unpack.ReadClientCommand(ref buf, out Command command, out ServerBound target);
				return (ok, (command, target));
			},
			cancellationToken);

		if (!ok)
		{
			throw new Socks5ProtocolErrorException(@"Incomplete SOCKS5 request.", Socks5Reply.GeneralFailure);
		}

		return result;
	}

	internal static ValueTask<FlushResult> SendReplyAsync(PipeWriter output, Socks5Reply reply, ServerBound bound, CancellationToken cancellationToken)
	{
		return output.WriteAndFlushAsync(
			Constants.MaxCommandLength,
			(reply, bound),
			static (state, span) => Pack.ServerReply(state.reply, state.bound, span),
			cancellationToken);
	}

	internal static ProxyDestination RentDestination(in ServerBound target, out byte[] rentedBuffer)
	{
		rentedBuffer = ArrayPool<byte>.Shared.Rent(target.Host.Length);
		target.Host.Span.CopyTo(rentedBuffer);
		return new ProxyDestination(rentedBuffer.AsMemory(0, target.Host.Length), target.Port);
	}

	internal static SocketAddress CloneSocketAddress(SocketAddress socketAddress)
	{
		SocketAddress clone = new(socketAddress.Family, socketAddress.Size);
		socketAddress.Buffer.CopyTo(clone.Buffer);
		return clone;
	}

	/// <summary>
	/// sockaddr layout: port at bytes [2..4], address at IPv4 [4..8] / IPv6 [8..24].
	/// </summary>
	internal static (int Offset, int Length) SockAddrSlice(AddressFamily family)
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

	internal static async Task MonitorControlChannelAsync(PipeReader input, CancellationTokenSource linkedCts)
	{
		try
		{
			while (true)
			{
				ReadResult result = await input.ReadAsync(linkedCts.Token);
				input.AdvanceTo(result.Buffer.End);

				if (result.IsCompleted)
				{
					break;
				}
			}
		}
		catch (OperationCanceledException) { }
		finally
		{
			await linkedCts.CancelAsync();
		}
	}

	internal static async Task RelayClientToRemoteAsync(
		Socket relaySocket,
		IPacketConnection packetConnection,
		SocketAddress expectedUdpSource,
		Action<SocketAddress> onPacketAccepted,
		CancellationToken cancellationToken)
	{
		byte[] buffer = ArrayPool<byte>.Shared.Rent(Constants.MaxUdpDatagramLength);
		byte[] hostBuffer = ArrayPool<byte>.Shared.Rent(byte.MaxValue);

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

				// RFC 1928 §7: a UDP relay server MUST drop any datagram it
				// cannot or will not relay — never let a single bad packet
				// tear down the entire relay loop.
				if (!Unpack.TryUdp(buffer.AsMemory(0, received), out Socks5UdpReceivePacket packet))
				{
					continue;
				}

				onPacketAccepted(senderSa);

				packet.Host.Span.CopyTo(hostBuffer);
				ProxyDestination dest = new(hostBuffer.AsMemory(0, packet.Host.Length), packet.Port);

				try
				{
					await packetConnection.SendToAsync(packet.Data, dest, cancellationToken);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					// Transient send failure — drop this datagram, keep relay alive.
				}
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
			ArrayPool<byte>.Shared.Return(hostBuffer);
		}
	}

	internal static async Task RelayRemoteToClientAsync(
		Socket relaySocket,
		IPacketConnection packetConnection,
		Task firstClientReady,
		Func<SocketAddress> getClientSa,
		CancellationToken cancellationToken)
	{
		await firstClientReady.WaitAsync(cancellationToken);

		byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(Constants.MaxUdpDatagramLength);
		byte[] sendBuffer = ArrayPool<byte>.Shared.Rent(Constants.MaxUdpHandshakeHeaderLength + Constants.MaxUdpDatagramLength);

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

				try
				{
					await relaySocket.SendToAsync(
						sendBuffer.AsMemory(0, packedLength),
						SocketFlags.None,
						getClientSa(),
						cancellationToken);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					// Transient send failure — drop this datagram, keep relay alive.
				}
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(receiveBuffer);
			ArrayPool<byte>.Shared.Return(sendBuffer);
		}
	}
}
