namespace HttpProxy;

internal readonly record struct HttpHeaders(bool IsConnect, string Hostname, ushort Port, long ContentLength, bool IsChunked);
