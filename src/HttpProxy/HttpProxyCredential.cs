namespace HttpProxy;

/// <summary>
/// Credentials for HTTP proxy authentication (Basic scheme).
/// </summary>
/// <param name="UserName">The proxy user name.</param>
/// <param name="Password">The proxy password.</param>
public sealed record HttpProxyCredential(string UserName, string Password);
