namespace HttpProxy;

internal readonly record struct HttpHeaders(
	bool IsConnect,
	ReadOnlyMemory<byte> Hostname,
	ushort Port,
	long? ContentLength,
	ReadOnlyMemory<byte> ProxyAuthorization);
