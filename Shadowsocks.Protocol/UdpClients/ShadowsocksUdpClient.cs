using Microsoft.Extensions.Logging;
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
		private readonly ILogger _logger;
		private readonly ShadowsocksServerInfo _serverInfo;

		public const int MaxUDPSize = ushort.MaxValue + 1;

		public UdpClient Client { get; }

		public ShadowsocksUdpClient(ILogger logger, ShadowsocksServerInfo serverInfo, bool isIPv6 = false)
		{
			_logger = logger;
			_serverInfo = serverInfo;

			Client = new UdpClient(0, isIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
		}

		public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken token)
		{
			var res = await Client.ReceiveAsync();
#if DEBUG
			_logger.LogDebug(@"UDP: Receive {0} bytes from {1}", res.Buffer.Length, res.RemoteEndPoint);
#endif
			using var decryptor = ShadowsocksCrypto.Create(_serverInfo.Method!, _serverInfo.Password!);

			var length = decryptor.DecryptUDP(res.Buffer, buffer.Span);
#if DEBUG
			_logger.LogDebug(@"UDP: Decode {0} => {1}", res.Buffer.Length, length);
#endif
			return length;
		}

		public async Task<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
		{
			using var encryptor = ShadowsocksCrypto.Create(_serverInfo.Method!, _serverInfo.Password!);
			var encBuffer = ArrayPool<byte>.Shared.Rent(MaxUDPSize);
			try
			{
				var length = encryptor.EncryptUDP(buffer.Span, encBuffer);
#if DEBUG
				_logger.LogDebug(@"UDP: Encode {0} => {1}", buffer.Length, length);
				_logger.LogDebug(@"UDP: Send {0} bytes to {1}", length, _serverInfo);
#endif
				return await Client.SendAsync(encBuffer, length, _serverInfo.Address, _serverInfo.Port);
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
