using Microsoft;
using Pipelines.Extensions;
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

		private const int MaxUdpSize = 0x10000;

		private readonly Socket _client;

		public ShadowsocksUdpClient(ShadowsocksServerInfo serverInfo)
		{
			Requires.NotNull(serverInfo, nameof(serverInfo));
			Requires.NotNullAllowStructs(serverInfo.Method, nameof(serverInfo));
			Requires.NotNullAllowStructs(serverInfo.Password, nameof(serverInfo));
			Requires.NotNullAllowStructs(serverInfo.Address, nameof(serverInfo));

			_serverInfo = serverInfo;

			_client = new Socket(SocketType.Dgram, ProtocolType.Udp);
			_client.Connect(serverInfo.Address, serverInfo.Port);
		}

		public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			var encBuffer = ArrayPool<byte>.Shared.Rent(MaxUdpSize);
			try
			{
				var res = await _client.ReceiveAsync(encBuffer, SocketFlags.None, cancellationToken);

				using var decryptor = ShadowsocksCrypto.Create(_serverInfo.Method!, _serverInfo.Password!);

				return decryptor.DecryptUDP(encBuffer.AsSpan(0, res), buffer.Span);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(encBuffer);
			}
		}

		public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			var encBuffer = ArrayPool<byte>.Shared.Rent(MaxUdpSize);
			try
			{
				using var encryptor = ShadowsocksCrypto.Create(_serverInfo.Method!, _serverInfo.Password!);
				var length = encryptor.EncryptUDP(buffer.Span, encBuffer);

				var sendLength = await _client.SendAsync(encBuffer.AsMemory(0, length), SocketFlags.None, cancellationToken);
				Report.IfNot(sendLength == length, @"Send Udp {0}/{1}", sendLength, length);
				return sendLength == length ? buffer.Length : default;
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

		public void Dispose()
		{
			_client.FullClose();
		}
	}
}
