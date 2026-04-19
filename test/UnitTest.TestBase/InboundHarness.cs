using Proxy.Abstractions;
using System.Net;
using System.Net.Sockets;

namespace UnitTest.TestBase;

public sealed class InboundHarness : IAsyncDisposable
{
	private readonly TcpListener _listener;
	private readonly CancellationTokenSource _acceptLoopCts;
	private readonly Task _acceptLoop;

	public ushort Port => (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;

	private InboundHarness(TcpListener listener, CancellationTokenSource acceptLoopCts, Task acceptLoop)
	{
		_listener = listener;
		_acceptLoopCts = acceptLoopCts;
		_acceptLoop = acceptLoop;
	}

	public static InboundHarness Start(IStreamInbound inbound, CancellationToken cancellationToken)
	{
		TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();

		CancellationTokenSource acceptLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		Task acceptLoop = TestAcceptLoop.RunAsync(listener, inbound, new DirectOutbound(), acceptLoopCts.Token);

		return new(listener, acceptLoopCts, acceptLoop);
	}

	public async ValueTask DisposeAsync()
	{
		await _acceptLoopCts.CancelAsync();

		try
		{
			await _acceptLoop;
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			_listener.Stop();
			_acceptLoopCts.Dispose();
		}
	}
}
