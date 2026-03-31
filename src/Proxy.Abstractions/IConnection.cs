using System.IO.Pipelines;
using System.Net;

namespace Proxy.Abstractions;

/// <summary>
/// A duplex pipe backed by a connection.
/// <see cref="IAsyncDisposable.DisposeAsync"/> releases the underlying transport.
/// </summary>
public interface IConnection : IDuplexPipe, IAsyncDisposable
{
	/// <summary>
	/// The local endpoint of the underlying transport, if available.
	/// </summary>
	SocketAddress? LocalEndPoint { get; }
}
