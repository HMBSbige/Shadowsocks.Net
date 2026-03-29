namespace Socks5;

/// <summary>
/// Represents a username/password credential for SOCKS5 authentication (RFC 1929).
/// </summary>
public readonly record struct UserPassAuth(ReadOnlyMemory<byte> UserName, ReadOnlyMemory<byte> Password)
{
	/// <inheritdoc/>
	public bool Equals(UserPassAuth other)
	{
		return UserName.Span.SequenceEqual(other.UserName.Span) && Password.Span.SequenceEqual(other.Password.Span);
	}

	/// <inheritdoc/>
	public override int GetHashCode()
	{
		HashCode hash = new();
		hash.AddBytes(UserName.Span);
		hash.AddBytes(Password.Span);
		return hash.ToHashCode();
	}
}
