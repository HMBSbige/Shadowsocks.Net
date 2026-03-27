namespace Socks5;

internal struct ServerBound
{
	/// <summary>
	/// An unspecified endpoint (IPv4 0.0.0.0:0) for SOCKS5 replies where the
	/// bound address is not meaningful — e.g. error replies or when the actual
	/// bound address is unavailable (RFC 1928, Section 6).
	/// </summary>
	public static readonly ServerBound Unspecified = CreateUnspecified();

	public AddressType Type;
	public HostField Host;
	public ushort Port;

	private static ServerBound CreateUnspecified()
	{
		ServerBound b = default;
		b.Type = AddressType.IPv4;
		"0.0.0.0"u8.CopyTo(b.Host.WriteBuffer);
		b.Host.Length = 7;
		return b;
	}
}
