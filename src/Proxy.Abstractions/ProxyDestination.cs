namespace Proxy.Abstractions;

/// <summary>
/// Represents a target address for proxy connections.
/// <see cref="Host"/> can be a domain name (e.g. "example.com") or an IP address string (e.g. "1.2.3.4", "::1").
/// <see cref="Port"/> must be a resolved concrete port (no sentinel 0).
/// </summary>
public readonly record struct ProxyDestination(string Host, ushort Port);
