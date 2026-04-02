using Proxy.Abstractions;

namespace UnitTest.TestBase;

public sealed class FailOnceSendPacketOutbound : IPacketOutbound
{
	public FailOnceSendPacketConnection PacketConnection { get; } = new();

	public ValueTask<IPacketConnection> CreatePacketConnectionAsync(CancellationToken cancellationToken = default)
	{
		return ValueTask.FromResult<IPacketConnection>(PacketConnection);
	}
}

public sealed class FailOnceSendPacketConnection : IPacketConnection
{
	private int _sendAttempts;
	public int SuccessfulSendCount;

	public ValueTask<int> SendToAsync(ReadOnlyMemory<byte> data, ProxyDestination destination, CancellationToken cancellationToken = default)
	{
		if (Interlocked.Increment(ref _sendAttempts) == 1)
		{
			throw new InvalidOperationException("Simulated send failure.");
		}

		Interlocked.Increment(ref SuccessfulSendCount);
		return ValueTask.FromResult(data.Length);
	}

	public async ValueTask<PacketReceiveResult> ReceiveFromAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		await Task.Delay(Timeout.Infinite, cancellationToken);
		return default; // unreachable — Delay throws on cancellation
	}

	public ValueTask DisposeAsync()
	{
		return ValueTask.CompletedTask;
	}
}
