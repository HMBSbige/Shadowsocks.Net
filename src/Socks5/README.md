# Socks5

An implementation of SOCKS5 inbound and outbound handlers built on [`Proxy.Abstractions`](../Proxy.Abstractions/README.md). Supports TCP `CONNECT`, UDP `ASSOCIATE`, and optional username/password authentication.

## Key Features

- `Socks5Inbound` accepts SOCKS5 client connections and relays them through a configurable outbound.
- `Socks5Outbound` connects to an upstream SOCKS5 server for both TCP and UDP traffic.
- Optional RFC 1929 username/password authentication via `UserPassAuth`.
- Built on `System.IO.Pipelines` and `Proxy.Abstractions` for composable proxy chains.

## Main Types

### `Socks5Inbound`

```csharp
public sealed partial class Socks5Inbound : IStreamInbound
{
    public Socks5Inbound(Socks5InboundOption option);

    public ValueTask HandleAsync(
        InboundContext context,
        IDuplexPipe clientPipe,
        IOutbound outbound,
        CancellationToken cancellationToken = default);
}
```

Handles incoming SOCKS5 requests from clients. Uses `IStreamOutbound` for `CONNECT` and `IPacketOutbound` for `UDP ASSOCIATE`. When `option.UserPassAuth` is provided, the inbound requires username/password authentication.

### `Socks5InboundOption`

```csharp
public record Socks5InboundOption
{
    public UserPassAuth? UserPassAuth { get; init; }
    public ILogger<Socks5Inbound>? Logger { get; init; }
    public IPAddress? UdpRelayBindAddress { get; init; }
}
```

Configures optional authentication, logging, and UDP relay binding for `Socks5Inbound`.

### `Socks5Outbound`

```csharp
public sealed class Socks5Outbound : IStreamOutbound, IPacketOutbound
{
    public Socks5Outbound(Socks5OutboundOption option);
    public ValueTask<IConnection> ConnectAsync(ProxyDestination destination, CancellationToken cancellationToken = default);
    public ValueTask<IPacketConnection> CreatePacketConnectionAsync(CancellationToken cancellationToken = default);
}
```

Connects to an upstream SOCKS5 server. Use `ConnectAsync` for proxied TCP connections and `CreatePacketConnectionAsync` for UDP relay traffic.

### `Socks5OutboundOption`

```csharp
public record Socks5OutboundOption
{
    public required IPAddress Address { get; init; }
    public required ushort Port { get; init; }
    public UserPassAuth? UserPassAuth { get; init; }
}
```

Configures the upstream SOCKS5 server endpoint and optional credentials used by `Socks5Outbound`.

### `UserPassAuth`

```csharp
public readonly record struct UserPassAuth(ReadOnlyMemory<byte> UserName, ReadOnlyMemory<byte> Password);
```

Represents RFC 1929 username/password credentials as raw bytes.

### `Socks5Utils`

```csharp
public static partial class Socks5Utils
{
    public static bool IsSocks5Header(this ReadOnlySequence<byte> buffer);
}
```

Low-level SOCKS5 protocol detection utilities operating directly on byte sequences. `IsSocks5Header` is an extension method on `ReadOnlySequence<byte>` that checks whether the buffer starts with a syntactically valid SOCKS5 client greeting header (correct protocol version, non-zero method count, and sufficient remaining data).
