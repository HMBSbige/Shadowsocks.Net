using Proxy.Abstractions;

namespace UnitTest.TestBase;

public sealed class SpyPacketOutbound : IPacketOutbound
{
	public SpyPacketConnection PacketConnection { get; } = new();

	public ValueTask<IPacketConnection> CreatePacketConnectionAsync(CancellationToken cancellationToken = default)
	{
		return ValueTask.FromResult<IPacketConnection>(PacketConnection);
	}
}

public sealed class SpyPacketConnection : IPacketConnection
{
	public int SendToCallCount;

	private readonly TaskCompletionSource _firstSendTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Completes when the first <see cref="SendToAsync"/> call is made.</summary>
	public Task FirstSendCompleted => _firstSendTcs.Task;

	public ValueTask<int> SendToAsync(ReadOnlyMemory<byte> data, ProxyDestination destination, CancellationToken cancellationToken = default)
	{
		Interlocked.Increment(ref SendToCallCount);
		_firstSendTcs.TrySetResult();
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
