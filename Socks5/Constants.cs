namespace Socks5
{
	internal static class Constants
	{
		public const byte ProtocolVersion = 0x05;
		public const byte Rsv = 0x00;
		public const byte AuthVersion = 0x01;
		public const int IPv4AddressBytesLength = 4;
		public const int IPv6AddressBytesLength = 16;
		public const int MaxAddressLength = 1 + 1 + byte.MaxValue;
		public const int MaxPortLength = 2;
		public const int MaxAddressPortLength = MaxAddressLength + MaxPortLength;
		public const int MaxCommandLength = 1 + 1 + 1 + MaxAddressPortLength;
		public const int MaxUsernamePasswordAuthLength = 1 + 1 + byte.MaxValue + 1 + byte.MaxValue;
		public const int MaxHandshakeClientMethodLength = 1 + 1 + byte.MaxValue;
		public const int MaxUdpHandshakeHeaderLength = 2 + 1 + MaxAddressPortLength;
	}
}
