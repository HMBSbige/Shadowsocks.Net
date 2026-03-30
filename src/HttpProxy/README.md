# HttpProxy

An [`IStreamInbound`](../Proxy.Abstractions/README.md) implementation that speaks the HTTP proxy protocol, supporting both `CONNECT` tunneling and plain HTTP forwarding.

## Key Features

- Handles HTTP `CONNECT` (tunneling) and regular HTTP request forwarding.
- Optional Basic authentication via `HttpProxyCredential`.
- Rewrites headers on the fly — strips proxy-specific headers and forces `Connection: close` for clean framing.
- Built on `System.IO.Pipelines` for high-performance, zero-copy I/O where possible.
- Pluggable outbound via `IOutbound` — connect directly or chain through another proxy.

## Main Types

### `HttpInbound`

```csharp
public partial class HttpInbound : IStreamInbound
{
    public HttpInbound(HttpProxyCredential? credential = null, ILogger<HttpInbound>? logger = null);
    public ValueTask HandleAsync(InboundContext context, IDuplexPipe clientPipe, IOutbound outbound, CancellationToken cancellationToken = default);
}
```

Forwards HTTP requests through a configurable outbound connector, rewriting headers on the fly. Reads the request from `clientPipe`, authenticates the client when credentials are configured, connects to the target host via `outbound`, and relays traffic bidirectionally.

For `CONNECT` requests, sends `200 Connection Established` and then links the two pipes directly. For plain HTTP requests, filters hop-by-hop headers and rewrites the request URI to origin-form before forwarding.

### `HttpProxyCredential`

```csharp
public sealed record HttpProxyCredential(string UserName, string Password);
```

Credentials for HTTP proxy authentication (Basic scheme). When supplied to `HttpInbound`, clients must present matching `Proxy-Authorization` headers or receive `407 Proxy Authentication Required`.

### `HttpUtils`

```csharp
public static partial class HttpUtils
{
    public static ReadOnlySpan<byte> HttpHeaderEnd { get; }   // \r\n\r\n
    public static ReadOnlySpan<byte> HttpNewLine { get; }     // \r\n
    public static bool IsHttpHeader(this ReadOnlySequence<byte> buffer);
}
```

Low-level HTTP header parsing and rewriting utilities operating directly on byte spans. `IsHttpHeader` is an extension method on `ReadOnlySequence<byte>` that checks whether the buffer contains a complete HTTP header block (terminated by `\r\n\r\n`) with a valid request line.
