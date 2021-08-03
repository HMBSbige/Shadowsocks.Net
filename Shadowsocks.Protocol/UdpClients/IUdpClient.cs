using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.UdpClients
{
	public interface IUdpClient : IAsyncDisposable
	{
		UdpClient Client { get; }

		Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken token);
		Task<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken token);
	}
}
