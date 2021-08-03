using Microsoft.Extensions.Logging;
using Shadowsocks.Protocol.ServersControllers;
using Shadowsocks.Protocol.UdpClients;
using Socks5.Enums;
using Socks5.Models;
using Socks5.Utils;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.LocalUdpServices
{
	public class Socks5UdpService : ILocalUdpService
	{
		private readonly ILogger _logger;

		private readonly IServersController _serversController;

		private readonly ConcurrentDictionary<IPEndPoint, IUdpClient> _clients;

		private readonly CancellationTokenSource _cts;

		public TimeSpan TimeOut { get; set; } = TimeSpan.FromSeconds(30);

		public Socks5UdpService(ILogger<Socks5UdpService> logger, IServersController serversController)
		{
			_logger = logger;
			_serversController = serversController;

			_clients = new ConcurrentDictionary<IPEndPoint, IUdpClient>();
			_cts = new CancellationTokenSource();
		}

		public async ValueTask<bool> IsHandleAsync(UdpReceiveResult receiveResult, UdpClient incoming)
		{
			Socks5UdpReceivePacket socks5UdpPacket;
			try
			{
				socks5UdpPacket = Unpack.Udp(receiveResult.Buffer);
			}
			catch (Exception ex)
			{
#if DEBUG
				_logger.LogDebug(ex, @"Socks5UdpService no handle");
#endif
				return false;
			}

			var target = socks5UdpPacket.Type switch
			{
				AddressType.Domain => socks5UdpPacket.Domain!,
				_ => socks5UdpPacket.Address!.ToString()
			};

			if (!_clients.TryGetValue(receiveResult.RemoteEndPoint, out var client))
			{
				client = await _serversController.GetServerUdpAsync(target);

				_clients.TryAdd(receiveResult.RemoteEndPoint, client);
				_ = TransferAsync(client, receiveResult.RemoteEndPoint, incoming, _cts.Token);
			}

			_logger.LogInformation(@"Udp Send to {0} via {1}", target, client);
			await client.SendAsync(receiveResult.Buffer.AsMemory(3), _cts.Token);

			return true;
		}

		private async Task TransferAsync(IUdpClient client, IPEndPoint source, UdpClient incoming, CancellationToken token)
		{
			try
			{
				var buffer = ArrayPool<byte>.Shared.Rent(3 + ShadowsocksUdpClient.MaxUDPSize);
				try
				{
					while (!token.IsCancellationRequested)
					{
						buffer[0] = buffer[1] = buffer[2] = 0;
						var task = client.ReceiveAsync(buffer.AsMemory(3), token);

						var resTask = await Task.WhenAny(Task.Delay(TimeOut, token), task);
						if (resTask != task)
						{
							break;
						}

						var length = await task;

						await incoming.SendAsync(buffer, length + 3, source);
					}
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(buffer);
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, @"Translation Error");
			}
			finally
			{
#if DEBUG
				_logger.LogDebug(@"Udp client Dispose: {0} <=> {1}", source, client);
#endif
				_clients.TryRemove(source, out _);
				await client.DisposeAsync();
			}
		}

		public void Stop()
		{
			_cts.Cancel();

			foreach (var (_, client) in _clients)
			{
				_ = client.DisposeAsync();
			}
		}
	}
}
