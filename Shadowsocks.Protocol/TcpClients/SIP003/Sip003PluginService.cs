using Microsoft;
using Shadowsocks.Protocol.Models;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Shadowsocks.Protocol.TcpClients.SIP003;

public class Sip003PluginService : ISip003PluginService
{
	private const string EnvRemoteHost = @"SS_REMOTE_HOST";
	private const string EnvRemotePort = @"SS_REMOTE_PORT";
	private const string EnvLocalHost = @"SS_LOCAL_HOST";
	private const string EnvLocalPort = @"SS_LOCAL_PORT";
	private const string EnvPluginOpts = @"SS_PLUGIN_OPTIONS";

	public IPEndPoint? LocalEndPoint { get; private set; }

	protected Process? Process;

	[MemberNotNull(nameof(Process))]
	public virtual void Start(ShadowsocksServerInfo info)
	{
		Verify.NotDisposed(this);
		Verify.Operation(Process is null || Process.HasExited, @"Process has started!");
		Requires.NotNullAllowStructs(info.Plugin, nameof(info.Plugin));

		LocalEndPoint = GetFreeLocalEndpoint();

		Process = new Process
		{
			StartInfo = new ProcessStartInfo(info.Plugin)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				Environment =
				{
					[EnvRemoteHost] = info.Address,
					[EnvRemotePort] = info.Port.ToString(),
					[EnvLocalHost] = LocalEndPoint.Address.ToString(),
					[EnvLocalPort] = LocalEndPoint.Port.ToString(),
					[EnvPluginOpts] = info.PluginOpts
				}
			}
		};

		Process.Start();

		static IPEndPoint GetFreeLocalEndpoint()
		{
			TcpListener listener = Socket.OSSupportsIPv6 ?
				new TcpListener(IPAddress.IPv6Loopback, default) { Server = { DualMode = true } } :
				new TcpListener(IPAddress.Loopback, default);
			try
			{
				listener.Start();
				return (IPEndPoint)listener.LocalEndpoint;
			}
			finally
			{
				listener.Stop();
			}
		}
	}

	public bool IsDisposed { get; private set; }

	public virtual void Dispose()
	{
		if (IsDisposed)
		{
			return;
		}

		if (Process is null)
		{
			return;
		}

		try
		{
			if (!Process.HasExited)
			{
				Process.Kill();
			}
		}
		catch
		{
			// ignored
		}
		finally
		{
			Process.Dispose();
			Process = null;
			IsDisposed = true;
			GC.SuppressFinalize(this);
		}
	}
}
