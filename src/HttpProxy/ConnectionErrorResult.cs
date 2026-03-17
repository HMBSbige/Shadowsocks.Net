namespace HttpProxy;

internal enum ConnectionErrorResult
{
	UnknownError,
	InvalidRequest,
	AuthenticationError,
	HostUnreachable,
	ConnectionRefused,
	ConnectionReset,
}
