namespace HttpProxy.Enums
{
	internal enum ConnectionErrorResult
	{
		UnknownError,
		InvalidRequest,
		AuthenticationError,
		HostUnreachable,
		ConnectionRefused,
		ConnectionReset,
	}
}
