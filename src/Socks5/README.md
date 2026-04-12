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
    public Socks5Inbound(
        UserPassAuth? credential = null,
        ILogger<Socks5Inbound>? logger = null,
        IPAddress? udpRelayBindAddress = null);

    public ValueTask HandleAsync(
        InboundContext context,
        IDuplexPipe clientPipe,
        IOutbound outbound,
        CancellationToken cancellationToken = default);
}
```

Handles incoming SOCKS5 requests from clients. Uses `IStreamOutbound` for `CONNECT` and `IPacketOutbound` for `UDP ASSOCIATE`. When `credential` is provided, the inbound requires username/password authentication.

### `Socks5Outbound`

```csharp
public sealed class Socks5Outbound(Socks5CreateOption option) : IStreamOutbound, IPacketOutbound
{
    public ValueTask<IConnection> ConnectAsync(ProxyDestination destination, CancellationToken cancellationToken = default);
    public ValueTask<IPacketConnection> CreatePacketConnectionAsync(CancellationToken cancellationToken = default);
}
```

Connects to an upstream SOCKS5 server. Use `ConnectAsync` for proxied TCP connections and `CreatePacketConnectionAsync` for UDP relay traffic.

### `Socks5CreateOption`

```csharp
public record Socks5CreateOption
{
    public IPAddress? Address { get; set; }
    public ushort Port { get; set; }
    public UserPassAuth? UserPassAuth { get; set; }
}
```

Configures the upstream SOCKS5 server endpoint and optional credentials used by `Socks5Outbound`.

### `UserPassAuth`

```csharp
public readonly record struct UserPassAuth(ReadOnlyMemory<byte> UserName, ReadOnlyMemory<byte> Password);
```

Represents RFC 1929 username/password credentials as raw bytes.
