using System.Text.Json.Serialization;

namespace Shadowsocks.Protocol.Models;

public record ShadowsocksServerInfo(Guid Id)
{
	[JsonPropertyName(@"id")]
	public Guid Id { get; set; } = Id;

	[JsonPropertyName(@"remarks")]
	public string? Remarks { get; set; }

	[JsonPropertyName(@"server")]
	public string? Address { get; set; }

	[JsonPropertyName(@"server_port")]
	public ushort Port { get; set; }

	[JsonPropertyName(@"password")]
	public string? Password { get; set; }

	[JsonPropertyName(@"method")]
	public string? Method { get; set; }

	[JsonPropertyName(@"plugin")]
	public string? Plugin { get; set; }

	[JsonPropertyName(@"plugin_opts")]
	public string? PluginOpts { get; set; }

	public ShadowsocksServerInfo() : this(Guid.NewGuid())
	{
	}

	public override string? ToString()
	{
		if (string.IsNullOrEmpty(Remarks))
		{
			return Remarks;
		}

		if (Address is not null)
		{
			return $@"{Address}:{Port}";
		}

		if (Id != default)
		{
			return Id.ToString();
		}

		return base.ToString();
	}
}
