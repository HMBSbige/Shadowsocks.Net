using System.IO.Pipelines;

namespace Proxy.Abstractions;

/// <summary>
/// A duplex pipe backed by a connection.
/// <see cref="IAsyncDisposable.DisposeAsync"/> releases the underlying transport.
/// </summary>
public interface IConnection : IDuplexPipe, IAsyncDisposable;
