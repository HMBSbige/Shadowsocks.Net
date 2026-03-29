namespace Proxy.Abstractions;

/// <summary>
/// Represents a target address for proxy connections.
/// <see cref="Host"/> contains the raw bytes of a domain name (e.g. "example.com") or an IP address (e.g. "1.2.3.4", "::1").
/// The caller must ensure the backing memory remains valid for the lifetime of this value.
/// </summary>
public readonly record struct ProxyDestination(ReadOnlyMemory<byte> Host, ushort Port)
{
	/// <inheritdoc/>
	public bool Equals(ProxyDestination other)
	{
		return Host.Span.SequenceEqual(other.Host.Span) && Port == other.Port;
	}

	/// <inheritdoc/>
	public override int GetHashCode()
	{
		HashCode hash = new();
		hash.AddBytes(Host.Span);
		hash.Add(Port);
		return hash.ToHashCode();
	}
}
