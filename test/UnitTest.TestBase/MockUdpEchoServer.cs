using System.Net;
using System.Net.Sockets;

namespace UnitTest.TestBase;

/// <summary>
/// A simple UDP echo server for testing: echoes back whatever it receives.
/// </summary>
public sealed class MockUdpEchoServer : IDisposable
{
	private readonly UdpClient _listener;
	private readonly CancellationTokenSource _cts = new();

	public int Port => ((IPEndPoint)_listener.Client.LocalEndPoint!).Port;

	public MockUdpEchoServer()
	{
		_listener = new UdpClient(AddressFamily.InterNetworkV6);
		_listener.Client.DualMode = true;
		_listener.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
	}

	public void Start()
	{
		_ = RunAsync();
	}

	private async Task RunAsync()
	{
		try
		{
			while (!_cts.IsCancellationRequested)
			{
				UdpReceiveResult result = await _listener.ReceiveAsync(_cts.Token);
				await _listener.SendAsync(result.Buffer, result.RemoteEndPoint, _cts.Token);
			}
		}
		catch (OperationCanceledException) { }
	}

	public void Dispose()
	{
		_cts.Cancel();
		_listener.Dispose();
		_cts.Dispose();
	}
}
