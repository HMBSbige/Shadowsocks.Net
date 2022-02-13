namespace Shadowsocks.Protocol.Models;

public record ShadowsocksServerInfo
{
	public string? Address { get; set; }

	public ushort Port { get; set; }

	public string? Password { get; set; }

	public string? Method { get; set; }

	public string? Remark { get; set; }

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
