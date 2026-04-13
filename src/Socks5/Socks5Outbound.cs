using Pipelines.Extensions;
using Proxy.Abstractions;
using Socks5.Protocol;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Socks5;

/// <summary>
/// Connects to an upstream SOCKS5 server and creates proxied TCP or UDP connections through it.
/// </summary>
/// <param name="option">Configuration for the upstream SOCKS5 server and optional credentials.</param>
public sealed class Socks5Outbound(Socks5CreateOption option) : IStreamOutbound, IPacketOutbound
{
	/// <summary>
	/// Opens a TCP connection to <paramref name="destination"/> through the configured SOCKS5 server.
	/// </summary>
	/// <param name="destination">The remote host and port to connect to.</param>
	/// <param name="cancellationToken">Cancellation token for the connect operation.</param>
	/// <returns>A proxied TCP connection.</returns>
	public async ValueTask<IConnection> ConnectAsync(ProxyDestination destination, CancellationToken cancellationToken = default)
	{
		(Socket socket, IDuplexPipe pipe) = await HandshakeAsync(cancellationToken);

		try
		{
			await Socks5Utils.SendCommandAsync(pipe, Command.Connect, destination.Host, destination.Port, cancellationToken);
			return new SocketConnection(socket, pipe);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Creates a UDP association with the configured SOCKS5 server.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token for the setup operation.</param>
	/// <returns>A packet connection that relays UDP datagrams through the SOCKS5 server.</returns>
	public async ValueTask<IPacketConnection> CreatePacketConnectionAsync(CancellationToken cancellationToken = default)
	{
		(Socket controlSocket, IDuplexPipe pipe) = await HandshakeAsync(cancellationToken);

		try
		{
			byte[] host = option.Address?.AddressFamily is AddressFamily.InterNetworkV6 ? Socks5Utils.IPv6Unspecified : Socks5Utils.IPv4Unspecified;
			ServerBound bound = await Socks5Utils.SendCommandAsync(pipe, Command.UdpAssociate, host, 0, cancellationToken);

			Socket udpSocket = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };

			try
			{
				// Connect UDP socket to the relay endpoint
				switch (bound.Type)
				{
					case AddressType.IPv4:
					case AddressType.IPv6:
					{
						if (!IPAddress.TryParse(bound.Host.Span, out IPAddress? boundAddr))
						{
							throw new Socks5ProtocolErrorException("Invalid IP in server reply", Socks5Reply.GeneralFailure);
						}

						IPAddress unspecified = bound.Type is AddressType.IPv4 ? IPAddress.Any : IPAddress.IPv6Any;

						if (Equals(boundAddr, unspecified))
						{
							boundAddr = option.Address!;
						}

						await udpSocket.ConnectAsync(boundAddr, bound.Port, cancellationToken);
						break;
					}
					case AddressType.Domain:
					{
						string domain = Encoding.ASCII.GetString(bound.Host.Span);
						await udpSocket.ConnectAsync(domain, bound.Port, cancellationToken);
						break;
					}
				}

				return new Socks5PacketConnection(controlSocket, udpSocket);
			}
			catch
			{
				udpSocket.Dispose();
				throw;
			}
		}
		catch
		{
			controlSocket.Dispose();
			throw;
		}
	}

	private async ValueTask<(Socket socket, IDuplexPipe pipe)> HandshakeAsync(CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(option.Address);

		option.UserPassAuth?.ThrowIfInvalid();

		Socket socket = new(option.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

		try
		{
			await socket.ConnectAsync(option.Address, option.Port, cancellationToken);
			IDuplexPipe pipe = socket.AsDuplexPipe();
			await HandshakeWithAuthAsync(pipe, cancellationToken);
			return (socket, pipe);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	private async ValueTask HandshakeWithAuthAsync(IDuplexPipe pipe, CancellationToken cancellationToken)
	{
		Method[] clientMethods = option.UserPassAuth is not null ? Socks5Utils.MethodsWithAuth : Socks5Utils.MethodsNoAuth;

		Method replyMethod = await Socks5Utils.HandshakeMethodAsync(pipe, clientMethods, cancellationToken);

		switch (replyMethod)
		{
			case Method.NoAuthentication:
				return;
			case Method.UsernamePassword when option.UserPassAuth is { } credential:
				await Socks5Utils.AuthAsync(pipe, credential, cancellationToken);
				break;
			default:
				throw new Socks5MethodUnsupportedException($@"Error method: {replyMethod}", replyMethod);
		}
	}

	private sealed class SocketConnection(Socket socket, IDuplexPipe pipe) : IConnection
	{
		public SocketAddress? LocalEndPoint { get; } = socket.LocalEndPoint?.Serialize();

		public PipeReader Input => pipe.Input;

		public PipeWriter Output => pipe.Output;

		public ValueTask DisposeAsync()
		{
			socket.FullClose();
			return ValueTask.CompletedTask;
		}
	}

	private sealed class Socks5PacketConnection(Socket controlSocket, Socket udpSocket) : IPacketConnection
	{
		public async ValueTask<int> SendToAsync(ReadOnlyMemory<byte> data, ProxyDestination destination, CancellationToken cancellationToken = default)
		{
			byte[] buffer = ArrayPool<byte>.Shared.Rent(Constants.MaxUdpHandshakeHeaderLength + data.Length);

			try
			{
				int length = Pack.Udp(buffer, destination.Host.Span, destination.Port, data.Span);
				await udpSocket.SendAsync(buffer.AsMemory(0, length), SocketFlags.None, cancellationToken);
				return data.Length;
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}

		public async ValueTask<PacketReceiveResult> ReceiveFromAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(0x10000);

			try
			{
				while (true)
				{
					int length = await udpSocket.ReceiveAsync(receiveBuffer.AsMemory(), SocketFlags.None, cancellationToken);

					if (!Unpack.TryUdp(receiveBuffer.AsMemory(0, length), out Socks5UdpReceivePacket packet))
					{
						continue;
					}

					if (packet.Data.Length > buffer.Length)
					{
						throw new ArgumentException
						(
							$"Receive buffer ({buffer.Length} bytes) is too small for the received packet ({packet.Data.Length} bytes).",
							nameof(buffer)
						);
					}

					packet.Data.Span.CopyTo(buffer.Span);

					return new PacketReceiveResult
					{
						BytesReceived = packet.Data.Length,
						RemoteDestination = new ProxyDestination(packet.Host.Span.ToArray(), packet.Port)
					};
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(receiveBuffer);
			}
		}

		public ValueTask DisposeAsync()
		{
			udpSocket.Dispose();
			controlSocket.FullClose();
			return ValueTask.CompletedTask;
		}
	}
}
