# Proxy.Abstractions

Provides abstractions for building proxy pipelines with [`System.IO.Pipelines`](https://learn.microsoft.com/dotnet/api/system.io.pipelines).

## Key Features

- Decouples inbound protocol handling from outbound connection strategies.
- Built on `IDuplexPipe` for high-performance, allocation-friendly I/O.
- Enables composable proxy chains (e.g. HTTP proxy over SOCKS5).

## Main Types

### `IInbound`

```csharp
public interface IInbound
{
    ValueTask HandleAsync(IDuplexPipe clientPipe, IOutbound outbound, CancellationToken cancellationToken = default);
}
```

Handles an inbound client connection at the protocol level. An implementation parses the proxy protocol from `clientPipe` (e.g. HTTP `CONNECT`, SOCKS5 handshake), extracts the target destination, connects via `outbound`, and relays traffic bidirectionally.

### `IOutbound`

```csharp
public interface IOutbound
{
    ValueTask<IConnection> ConnectAsync(ProxyDestination destination, CancellationToken cancellationToken = default);
}
```

Creates outbound connections to a `ProxyDestination`. By swapping implementations, the same `IInbound` can connect directly via TCP, or tunnel through another proxy (SOCKS5, HTTP proxy, etc.).

### `IConnection`

```csharp
public interface IConnection : IDuplexPipe, IAsyncDisposable;
```

A duplex pipe backed by a connection. Provides `PipeReader` / `PipeWriter` for I/O. `DisposeAsync` releases the underlying transport.

### `ProxyDestination`

```csharp
public readonly record struct ProxyDestination(ReadOnlyMemory<byte> Host, ushort Port);
```

Represents a target address as raw bytes. `Host` contains the UTF-8/ASCII bytes of a domain name (`"example.com"`) or an IP address (`"1.2.3.4"`, `"::1"`). The caller must ensure the backing memory remains valid for the lifetime of this value.
