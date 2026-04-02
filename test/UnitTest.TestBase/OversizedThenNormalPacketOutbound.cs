using Proxy.Abstractions;

namespace UnitTest.TestBase;

/// <summary>
/// Provides a packet connection whose <see cref="IPacketConnection.ReceiveFromAsync"/>
/// first returns an oversized payload (65 500 bytes) that, after SOCKS5 UDP header
/// wrapping, exceeds the 65 507-byte UDP maximum — causing the relay socket's
/// <c>SendToAsync</c> to throw <see cref="System.Net.Sockets.SocketException"/>
/// with <see cref="System.Net.Sockets.SocketError.MessageSize"/>.
/// The second call returns a small, normal payload.
/// Subsequent calls block until cancellation.
/// </summary>
public sealed class OversizedThenNormalPacketOutbound : IPacketOutbound
{
	public OversizedThenNormalPacketConnection PacketConnection { get; } = new();

	public ValueTask<IPacketConnection> CreatePacketConnectionAsync(CancellationToken cancellationToken = default)
	{
		return ValueTask.FromResult<IPacketConnection>(PacketConnection);
	}
}

public sealed class OversizedThenNormalPacketConnection : IPacketConnection
{
	private int _receiveAttempts;

	/// <summary>Number of packets the relay successfully received from this connection (i.e. ReceiveFromAsync returned).</summary>
	public int ReceiveReturnCount;

	public ValueTask<int> SendToAsync(ReadOnlyMemory<byte> data, ProxyDestination destination, CancellationToken cancellationToken = default)
	{
		return ValueTask.FromResult(data.Length);
	}

	public async ValueTask<PacketReceiveResult> ReceiveFromAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		int attempt = Interlocked.Increment(ref _receiveAttempts);

		if (attempt == 1)
		{
			// 65500-byte payload + 10-byte SOCKS5 IPv4 UDP header = 65510 > 65507 max UDP payload.
			buffer.Span.Slice(0, 65500).Clear();
			Interlocked.Increment(ref ReceiveReturnCount);
			return new PacketReceiveResult
			{
				BytesReceived = 65500,
				RemoteDestination = new ProxyDestination("127.0.0.1"u8.ToArray(), 9999)
			};
		}

		if (attempt == 2)
		{
			byte[] payload = "hello"u8.ToArray();
			payload.CopyTo(buffer);
			Interlocked.Increment(ref ReceiveReturnCount);
			return new PacketReceiveResult
			{
				BytesReceived = payload.Length,
				RemoteDestination = new ProxyDestination("127.0.0.1"u8.ToArray(), 9999)
			};
		}

		// 3rd+ calls: block until cancellation.
		await Task.Delay(Timeout.Infinite, cancellationToken);
		return default; // unreachable
	}

	public ValueTask DisposeAsync()
	{
		return ValueTask.CompletedTask;
	}
}
