namespace Socks5.Enums
{
	public enum Status
	{
		/// <summary>
		/// Before handshake and authentication.
		/// </summary>
		Initial,

		/// <summary>
		/// After handshake and authentication, able to send data.
		/// </summary>
		Established,

		/// <summary>
		/// Connection closed, can not reuse.
		/// </summary>
		Closed
	}
}
