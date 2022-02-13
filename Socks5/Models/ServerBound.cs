using Socks5.Enums;
using System.Net;

namespace Socks5.Models;

public struct ServerBound
{
	public AddressType Type;
	public IPAddress? Address;
	public string? Domain;
	public ushort Port;
}
