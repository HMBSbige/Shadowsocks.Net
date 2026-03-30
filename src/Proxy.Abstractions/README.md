# Proxy.Abstractions

Provides abstractions for building proxy pipelines — stream I/O via [`System.IO.Pipelines`](https://learn.microsoft.com/dotnet/api/system.io.pipelines) and packet I/O via `IPacketConnection`.

## Key Features

- Decouples inbound protocol handling from outbound connection strategies.
- Stream I/O built on `IDuplexPipe` for high-performance, allocation-friendly I/O.
- Packet I/O built on `IPacketConnection` for message-oriented protocols.
- Enables composable proxy chains (e.g. HTTP proxy over SOCKS5).

## Main Types

### `IInbound`

```csharp
public interface IInbound;
```

Base type for all inbound implementations. Concrete capabilities are exposed via `IStreamInbound` and `IPacketInbound`.
These names describe the I/O shape, not a specific transport protocol: stream inbounds handle ordered bidirectional byte streams, while packet inbounds handle discrete messages with per-message addressing.

### `IStreamInbound`

```csharp
public interface IStreamInbound : IInbound
{
    ValueTask HandleAsync(InboundContext context, IDuplexPipe clientPipe, IOutbound outbound, CancellationToken cancellationToken = default);
}
```

Handles a stream-oriented inbound client connection. An implementation parses the proxy protocol from `clientPipe` (e.g. HTTP `CONNECT`, SOCKS5 handshake), extracts the target destination, connects via `outbound`, and relays traffic bidirectionally. `InboundContext` carries per-connection metadata (client/local endpoints) supplied by the accept loop.

### `IPacketInbound`

```csharp
public interface IPacketInbound : IInbound
{
    ValueTask HandleAsync(InboundContext context, IPacketConnection clientPackets, IOutbound outbound, CancellationToken cancellationToken = default);
}
```

Handles a packet-oriented inbound connection. An implementation reads packets from `clientPackets`, processes them (e.g. decryption, session management), and relays traffic via `outbound`.

### `InboundContext`

```csharp
public sealed class InboundContext
{
    public required IPAddress ClientAddress { get; init; }
    public required ushort ClientPort { get; init; }
    public required IPAddress LocalAddress { get; init; }
    public required ushort LocalPort { get; init; }
}
```

Per-connection metadata supplied by the accept loop: the client's remote address/port and the local address/port on which the connection was accepted.

### `IOutbound`

```csharp
public interface IOutbound;
```

Base type for all outbound implementations. Concrete capabilities are exposed via `IStreamOutbound` and `IPacketOutbound`.
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

Represents a target address for proxy connections. `Host` contains the raw bytes of a domain name (e.g. `"example.com"`) or an IP address (e.g. `"1.2.3.4"`, `"::1"`). The caller must ensure the backing memory remains valid for the lifetime of this value.

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
