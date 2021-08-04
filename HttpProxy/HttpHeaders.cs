using System;

namespace HttpProxy
{
	public record HttpHeaders
	{
		public bool IsConnect => Method is not null && Method.Equals(@"CONNECT", StringComparison.OrdinalIgnoreCase);

		public string? Method { get; set; }
		public string? HostUriString { get; set; }
		public string HttpVersion { get; set; } = @"HTTP/1.1";

		public string? Hostname { get; set; }
		public ushort Port { get; set; } = 80;

		public int ContentLength { get; set; }

		public string? Request { get; set; }
	}
}
