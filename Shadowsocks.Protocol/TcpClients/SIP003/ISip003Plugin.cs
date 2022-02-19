using Microsoft;
using Shadowsocks.Protocol.Models;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Shadowsocks.Protocol.TcpClients.SIP003;

public interface ISip003Plugin : IDisposableObservable
{
	IPEndPoint? LocalEndPoint { get; }

	[MemberNotNull(nameof(LocalEndPoint))]
	void Start(ShadowsocksServerInfo info);
}
