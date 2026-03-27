using Pipelines.Extensions;
using Proxy.Abstractions;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UnitTest.TestBase;

/// <summary>
/// Connects directly to the target host.
/// </summary>
public sealed class DirectOutbound : IStreamOutbound, IPacketOutbound
{
	/// <inheritdoc />
	public async ValueTask<IConnection> ConnectAsync(ProxyDestination destination, CancellationToken cancellationToken)
	{
		Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

		try
		{
			if (IPAddress.TryParse(destination.Host.Span, out IPAddress? ip))
			{
				await socket.ConnectAsync(ip, destination.Port, cancellationToken);
			}
			else
			{
				await socket.ConnectAsync(Encoding.ASCII.GetString(destination.Host.Span), destination.Port, cancellationToken);
			}

			return new SocketConnection(socket);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	/// <inheritdoc />
	public ValueTask<IPacketConnection> CreatePacketConnectionAsync(ProxyDestination destination, CancellationToken cancellationToken)
	{
		return ValueTask.FromResult<IPacketConnection>(new DirectPacketConnection());
	}

	private sealed class SocketConnection(Socket socket) : IConnection
	{
		private readonly IDuplexPipe _pipe = socket.AsDuplexPipe();

		public PipeReader Input => _pipe.Input;

		public PipeWriter Output => _pipe.Output;

		public ValueTask DisposeAsync()
		{
			socket.FullClose();
			return ValueTask.CompletedTask;
		}
	}

	private sealed class DirectPacketConnection : IPacketConnection
	{
		private readonly Socket _socket = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };

		public DirectPacketConnection()
		{
			_socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
		}

		public async ValueTask<int> SendToAsync(ReadOnlyMemory<byte> data, ProxyDestination destination, CancellationToken cancellationToken = default)
		{
			string host = Encoding.ASCII.GetString(destination.Host.Span);

			IPAddress address;

			if (IPAddress.TryParse(host, out IPAddress? parsed))
			{
				address = parsed;
			}
			else
			{
				IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
				address = addresses[0];
			}

			await _socket.SendToAsync(data, SocketFlags.None, new IPEndPoint(address, destination.Port), cancellationToken);
			return data.Length;
		}

		public async ValueTask<PacketReceiveResult> ReceiveFromAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			SocketReceiveFromResult result = await _socket.ReceiveFromAsync(
				buffer,
				SocketFlags.None,
				new IPEndPoint(IPAddress.IPv6Any, 0),
				cancellationToken);

			IPEndPoint remote = (IPEndPoint)result.RemoteEndPoint;
			IPAddress address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
			byte[] hostBytes = Encoding.ASCII.GetBytes(address.ToString());

			return new PacketReceiveResult
			{
				BytesReceived = result.ReceivedBytes,
				RemoteDestination = new ProxyDestination(hostBytes, (ushort)remote.Port)
			};
		}

		public ValueTask DisposeAsync()
		{
			_socket.FullClose();
			return ValueTask.CompletedTask;
		}
	}
}
