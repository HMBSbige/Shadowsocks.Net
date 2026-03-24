namespace Proxy.Abstractions;

/// <summary>
/// Base type for all outbound implementations.
/// Concrete capabilities are exposed via <see cref="IStreamOutbound"/> and <see cref="IPacketOutbound"/>.
/// </summary>
public interface IOutbound;
