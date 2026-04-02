using Proxy.Abstractions;
using System.IO.Pipelines;
using System.Net;

namespace UnitTest.TestBase;

public sealed class NullLocalEndPointOutbound : IStreamOutbound
{
	public ValueTask<IConnection> ConnectAsync(ProxyDestination destination, CancellationToken cancellationToken = default)
	{
		return ValueTask.FromResult<IConnection>(new NullLocalEndPointConnection());
	}
}

public sealed class NullLocalEndPointConnection : IConnection
{
	public SocketAddress? LocalEndPoint => null;

	public PipeReader Input { get; } = PipeReader.Create(Stream.Null);

	public PipeWriter Output { get; } = PipeWriter.Create(Stream.Null);

	public async ValueTask DisposeAsync()
	{
		await Input.CompleteAsync();
		await Output.CompleteAsync();
	}
}
