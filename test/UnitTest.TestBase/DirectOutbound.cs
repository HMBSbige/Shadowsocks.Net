using Pipelines.Extensions;
using Proxy.Abstractions;
using System.Buffers.Binary;
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
	public ValueTask<IPacketConnection> CreatePacketConnectionAsync(CancellationToken cancellationToken)
	{
		return ValueTask.FromResult<IPacketConnection>(new DirectPacketConnection());
	}

	private sealed class SocketConnection(Socket socket) : IConnection
	{
		private readonly IDuplexPipe _pipe = socket.AsDuplexPipe();

		public SocketAddress? LocalEndPoint { get; } = socket.LocalEndPoint?.Serialize();

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
		private readonly SocketAddress _sendSa = new(AddressFamily.InterNetworkV6);
		private readonly SocketAddress _recvSa = new(AddressFamily.InterNetworkV6);

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

			Span<byte> buf = _sendSa.Buffer.Span;
			BinaryPrimitives.WriteUInt16BigEndian(buf.Slice(2), destination.Port);

			if (address.AddressFamily is AddressFamily.InterNetwork)
			{
				buf.Slice(8, 10).Clear();
				buf[18] = 0xff;
				buf[19] = 0xff;
				address.TryWriteBytes(buf.Slice(20), out _);
				buf.Slice(24, 4).Clear();
			}
			else
			{
				address.TryWriteBytes(buf.Slice(8), out _);
				BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(24), (int)address.ScopeId);
			}

			await _socket.SendToAsync(data, SocketFlags.None, _sendSa, cancellationToken);
			return data.Length;
		}

		public async ValueTask<PacketReceiveResult> ReceiveFromAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			int received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, _recvSa, cancellationToken);

			Span<byte> buf = _recvSa.Buffer.Span;
			ushort port = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(2));
			IPAddress address = new(buf.Slice(8, 16));

			if (address.IsIPv4MappedToIPv6)
			{
				address = address.MapToIPv4();
			}

			byte[] hostBytes = Encoding.ASCII.GetBytes(address.ToString());

			return new PacketReceiveResult
			{
				BytesReceived = received,
				RemoteDestination = new ProxyDestination(hostBytes, port)
			};
		}

		public ValueTask DisposeAsync()
		{
			_socket.FullClose();
			return ValueTask.CompletedTask;
		}
	}
}
