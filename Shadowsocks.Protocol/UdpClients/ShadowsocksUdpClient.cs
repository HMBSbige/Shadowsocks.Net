using Microsoft;
using Shadowsocks.Crypto;
using Shadowsocks.Protocol.Models;
using System;
using System.Buffers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.UdpClients
{
	public class ShadowsocksUdpClient : IUdpClient
	{
		private readonly ShadowsocksServerInfo _serverInfo;

		private const int MaxUDPSize = 0x10000;

		public UdpClient Client { get; }

		public ShadowsocksUdpClient(ShadowsocksServerInfo serverInfo, bool isIPv6 = false)
		{
			_serverInfo = serverInfo;

			Client = new UdpClient(0, isIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
		}

		public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			//TODO .NET6.0
			var res = await Client.ReceiveAsync();

			using var decryptor = ShadowsocksCrypto.Create(_serverInfo.Method!, _serverInfo.Password!);

			return decryptor.DecryptUDP(res.Buffer, buffer.Span);
		}

		public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			using var encryptor = ShadowsocksCrypto.Create(_serverInfo.Method!, _serverInfo.Password!);
			var encBuffer = ArrayPool<byte>.Shared.Rent(MaxUDPSize);
			try
			{
				var length = encryptor.EncryptUDP(buffer.Span, encBuffer);

				//TODO .NET6.0
				var sendLength = await Client.SendAsync(encBuffer, length, _serverInfo.Address, _serverInfo.Port);
				Report.IfNot(sendLength == length, @"Send Udp {0}/{1}", sendLength, length);
				return sendLength == length ? buffer.Length : 0;
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(encBuffer);
			}
		}

		public override string? ToString()
		{
			return _serverInfo.ToString();
		}

		public ValueTask DisposeAsync()
		{
			Client.Dispose();

			return default;
		}
	}
}
