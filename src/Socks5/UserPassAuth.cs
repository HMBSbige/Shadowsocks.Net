namespace Socks5;

/// <summary>
/// Represents a username/password credential for SOCKS5 authentication (RFC 1929).
/// </summary>
public readonly record struct UserPassAuth(ReadOnlyMemory<byte> UserName, ReadOnlyMemory<byte> Password)
{
	/// <summary>
	/// Throws <see cref="ArgumentException"/> if <see cref="UserName"/> or <see cref="Password"/>
	/// violates the 1–255 byte length constraint (RFC 1929 §2).
	/// </summary>
	public void ThrowIfInvalid()
	{
		if (UserName.Length is 0 or > byte.MaxValue)
		{
			throw new ArgumentException("UserName must be 1–255 bytes (RFC 1929 §2).");
		}

		if (Password.Length is 0 or > byte.MaxValue)
		{
			throw new ArgumentException("Password must be 1–255 bytes (RFC 1929 §2).");
		}
	}

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
