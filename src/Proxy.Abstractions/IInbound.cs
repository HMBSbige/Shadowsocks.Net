namespace Proxy.Abstractions;

/// <summary>
/// Base type for all inbound implementations.
/// Concrete capabilities are exposed via <see cref="IStreamInbound"/> and <see cref="IPacketInbound"/>.
/// </summary>
public interface IInbound;
