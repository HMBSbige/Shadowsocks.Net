using Pipelines.Extensions;
using Proxy.Abstractions;
using Socks5.Protocol;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Socks5;

public sealed class Socks5Outbound(Socks5CreateOption option) : IStreamOutbound, IPacketOutbound
{
	private static readonly Method[] MethodsNoAuth = [Method.NoAuthentication];
	private static readonly Method[] MethodsWithAuth = [Method.NoAuthentication, Method.UsernamePassword];

	public async ValueTask<IConnection> ConnectAsync(ProxyDestination destination, CancellationToken cancellationToken = default)
	{
		(Socket socket, IDuplexPipe pipe) = await HandshakeAsync(cancellationToken);

		try
		{
			await SendCommandAsync(pipe, Command.Connect, destination.Host, destination.Port, cancellationToken);
			return new SocketConnection(socket, pipe);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	public async ValueTask<IPacketConnection> CreatePacketConnectionAsync(CancellationToken cancellationToken = default)
	{
		(Socket controlSocket, IDuplexPipe pipe) = await HandshakeAsync(cancellationToken);

		try
		{
			Socket udpSocket = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };

			try
			{
				udpSocket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
				IPEndPoint local = (IPEndPoint)udpSocket.LocalEndPoint!;

				byte[] hostBytes = new byte[Constants.MaxIpTextLength];
				local.Address.TryFormat(hostBytes, out int hostLen);
				ServerBound bound = await SendCommandAsync
				(
					pipe, Command.UdpAssociate,
					hostBytes.AsMemory(0, hostLen), (ushort)local.Port, cancellationToken
				);

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
		Method[] clientMethods = option.UserPassAuth is not null ? MethodsWithAuth : MethodsNoAuth;

		Method replyMethod = await HandshakeMethodAsync(pipe, clientMethods, cancellationToken);

		switch (replyMethod)
		{
			case Method.NoAuthentication:
				return;
			case Method.UsernamePassword when option.UserPassAuth is { } credential:
				await AuthAsync(pipe, credential, cancellationToken);
				break;
			default:
				throw new MethodUnsupportedException($@"Error method: {replyMethod}", replyMethod);
		}
	}

	private static async ValueTask<Method> HandshakeMethodAsync(IDuplexPipe pipe, Method[] clientMethods, CancellationToken cancellationToken)
	{
		await pipe.Output.WriteAsync(Constants.MaxHandshakeClientMethodLength, PackHandshake, cancellationToken);

		Method method = Method.NoAuthentication;
		await pipe.Input.ReadAsync(HandleResponse, cancellationToken);

		bool found = false;

		foreach (Method m in clientMethods)
		{
			if (m == method)
			{
				found = true;
				break;
			}
		}

		if (!found)
		{
			throw new MethodUnsupportedException($@"Server sent an unsupported method ({method}:0x{(byte)method:X2}).", method);
		}

		return method;

		int PackHandshake(Span<byte> span)
		{
			return Pack.Handshake(clientMethods, span);
		}

		ParseResult HandleResponse(ref ReadOnlySequence<byte> buffer)
		{
			return Unpack.ReadResponseMethod(ref buffer, out method) ? ParseResult.Success : ParseResult.NeedsMoreData;
		}
	}

	private static async ValueTask AuthAsync(IDuplexPipe pipe, UserPassAuth credential, CancellationToken cancellationToken)
	{
		await pipe.Output.WriteAsync(Constants.MaxUsernamePasswordAuthLength, PackUsernamePassword, cancellationToken);

		if (!await pipe.Input.ReadAsync(HandleResponse, cancellationToken))
		{
			throw new Socks5ProtocolErrorException(@"Auth failed!", Socks5Reply.ConnectionNotAllowed);
		}

		return;

		int PackUsernamePassword(Span<byte> span)
		{
			return Pack.UsernamePasswordAuth(credential, span);
		}

		static ParseResult HandleResponse(ref ReadOnlySequence<byte> buffer)
		{
			return Unpack.ReadResponseAuthReply(ref buffer) ? ParseResult.Success : ParseResult.NeedsMoreData;
		}
	}

	private static async ValueTask<ServerBound> SendCommandAsync(
		IDuplexPipe pipe, Command command,
		ReadOnlyMemory<byte> host, ushort port,
		CancellationToken cancellationToken)
	{
		await pipe.Output.WriteAsync(Constants.MaxCommandLength, PackClientCommand, cancellationToken);

		ServerBound bound = new();

		if (!await pipe.Input.ReadAsync(HandleResponse, cancellationToken))
		{
			throw new Socks5ProtocolErrorException(@"Send command failed!", Socks5Reply.CommandNotSupported);
		}

		return bound;

		int PackClientCommand(Span<byte> span)
		{
			return Pack.ClientCommand(command, host.Span, port, span);
		}

		ParseResult HandleResponse(ref ReadOnlySequence<byte> buffer)
		{
			return Unpack.ReadServerReplyCommand(ref buffer, out bound) ? ParseResult.Success : ParseResult.NeedsMoreData;
		}
	}

	private sealed class SocketConnection(Socket socket, IDuplexPipe pipe) : IConnection
	{
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
					Socks5UdpReceivePacket packet = Unpack.Udp(receiveBuffer.AsMemory(0, length));

					if (packet.Fragment is not 0x00)
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

					byte[] hostBytes = new byte[packet.Host.Length];
					packet.Host.Span.CopyTo(hostBytes);

					return new PacketReceiveResult
					{
						BytesReceived = packet.Data.Length,
						RemoteDestination = new ProxyDestination(hostBytes, packet.Port)
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
