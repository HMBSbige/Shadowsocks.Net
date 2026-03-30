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
    ValueTask HandleAsync(InboundContext context, IDuplexPipe clientPipe, IOutbound outbound, CancellationToken cancellationToken = default);
}
```

Handles an inbound client connection at the protocol level. An implementation parses the proxy protocol from `clientPipe` (e.g. HTTP `CONNECT`, SOCKS5 handshake), extracts the target destination, connects via `outbound`, and relays traffic bidirectionally. `InboundContext` carries per-connection metadata (client/local endpoints) supplied by the accept loop.

### `IOutbound`

```csharp
public interface IOutbound;
```

Base marker type for all outbound implementations. Concrete capabilities are exposed via `IStreamOutbound` and `IPacketOutbound`.
These names describe the I/O shape, not a specific transport protocol: stream outbounds provide an ordered bidirectional byte stream, while packet outbounds preserve per-message boundaries.

### `IStreamOutbound`

```csharp
public interface IStreamOutbound : IOutbound
{
    ValueTask<IConnection> ConnectAsync(ProxyDestination destination, CancellationToken cancellationToken = default);
}
```

Creates stream-oriented outbound connections. Typical examples include direct TCP connections, tunneled byte streams, or any other transport that behaves like an ordered bidirectional stream.

### `IPacketOutbound`

```csharp
public interface IPacketOutbound : IOutbound
{
    ValueTask<IPacketConnection> CreatePacketConnectionAsync(CancellationToken cancellationToken = default);
}
```

Creates packet-oriented outbound connections. Per-message destinations are specified via `IPacketConnection.SendToAsync`. Typical examples include UDP or any other transport that preserves message boundaries.

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

### `IPacketConnection`

```csharp
public interface IPacketConnection : IAsyncDisposable
{
    ValueTask<int> SendToAsync(ReadOnlyMemory<byte> data, ProxyDestination destination, CancellationToken cancellationToken = default);
    ValueTask<PacketReceiveResult> ReceiveFromAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}
```

A packet-oriented connection with per-message addressing. `SendToAsync` sends data to the specified destination. `ReceiveFromAsync` receives one packet and returns the number of bytes received along with the remote destination.

### `PacketReceiveResult`

```csharp
public readonly struct PacketReceiveResult
{
    public int BytesReceived { get; init; }
    public ProxyDestination RemoteDestination { get; init; }
}
```

Result of a packet receive operation, including the number of bytes received and the remote endpoint.
