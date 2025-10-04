using Shadowsocks.Protocol.Models;
using System.Runtime.Versioning;
using WindowsJobAPI;

namespace Shadowsocks.Protocol.TcpClients.SIP003;

[SupportedOSPlatform(@"Windows")]
public sealed class Sip003PluginWindows : Sip003Plugin
{
	private readonly JobObject _job = new();

	public override void Start(ShadowsocksServerInfo info)
	{
		base.Start(info);
		_job.AddProcess(Process);
	}

	public override void Dispose()
	{
		_job.Dispose();
		base.Dispose();
	}
}
