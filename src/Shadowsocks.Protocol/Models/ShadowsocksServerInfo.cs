using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json.Serialization;
using System.Web;

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
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Plugin { get; set; }

	[JsonPropertyName(@"plugin_opts")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? PluginOpts { get; set; }

	public ShadowsocksServerInfo() : this(Guid.NewGuid())
	{
	}

	public string ToSip002UriSchemeString()
	{
		if (string.IsNullOrEmpty(Address) || Port == default || string.IsNullOrEmpty(Password) || string.IsNullOrEmpty(Method))
		{
			return string.Empty;
		}

		UriBuilder builder = new(@"ss", Address, Port)
		{
			UserName = $@"{Method}:{Password}".ToBase64UrlSafe(),
			Fragment = HttpUtility.UrlPathEncode(Remarks)
		};

		if (!string.IsNullOrEmpty(Plugin))
		{
			NameValueCollection query = HttpUtility.ParseQueryString(builder.Query);
			string plugin = string.IsNullOrEmpty(PluginOpts) ? Plugin : $@"{Plugin};{PluginOpts}";
			query[@"plugin"] = plugin;
			builder.Query = query.ToString();
		}

		return builder.ToString();
	}

	public static bool TryParse(string? uri, [NotNullWhen(true)] out ShadowsocksServerInfo? serverInfo)
	{
		serverInfo = default;

		if (string.IsNullOrEmpty(uri) || !uri.StartsWith(@"ss://"))
		{
			return false;
		}

		try
		{
			UriBuilder builder = new(uri);
			if (string.IsNullOrEmpty(builder.Host)
				|| builder.Port is <= IPEndPoint.MinPort or > IPEndPoint.MaxPort
				|| string.IsNullOrEmpty(builder.UserName))
			{
				return false;
			}

			string methodPassword = builder.UserName.FromBase64UrlSafe();
			int i = methodPassword.LastIndexOf(':');
			if (i <= 0)
			{
				return false;
			}

			serverInfo = new ShadowsocksServerInfo
			{
				Remarks = HttpUtility.UrlDecode(builder.Fragment.TrimStart('#')),
				Address = builder.Host,
				Port = (ushort)builder.Port,
				Password = methodPassword[(i + 1)..],
				Method = methodPassword[..i]
			};

			NameValueCollection query = HttpUtility.ParseQueryString(builder.Query);
			string? plugin = query.Get(@"plugin");
			if (!string.IsNullOrEmpty(plugin))
			{
				int j = plugin.IndexOf(';');
				if (j <= 0)
				{
					serverInfo.Plugin = plugin;
				}
				else
				{
					serverInfo.Plugin = plugin[..j];
					serverInfo.PluginOpts = plugin[(j + 1)..];
				}
			}

			return true;
		}
		catch
		{
			return false;
		}
	}

	public override string? ToString()
	{
		if (!string.IsNullOrEmpty(Remarks))
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
