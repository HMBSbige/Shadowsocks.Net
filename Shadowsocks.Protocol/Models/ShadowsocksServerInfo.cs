namespace Shadowsocks.Protocol.Models
{
	public record ShadowsocksServerInfo
	{
		public string? Address { set; get; }

		public ushort Port { set; get; }

		public string? Password { set; get; }

		public string? Method { set; get; }

		public string? Remark { set; get; }

		public override string? ToString()
		{
			if (!string.IsNullOrEmpty(Remark))
			{
				return Remark;
			}

			if (Address is null)
			{
				return base.ToString();
			}

			return $@"{Address}:{Port}";
		}
	}
}
