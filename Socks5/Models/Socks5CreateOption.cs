using System.Net;

namespace Socks5.Models
{
	public record Socks5CreateOption
	{
		public IPAddress? Address { get; set; }
		public ushort Port { get; set; }
		public UsernamePassword? UsernamePassword { get; set; }
	}
}
