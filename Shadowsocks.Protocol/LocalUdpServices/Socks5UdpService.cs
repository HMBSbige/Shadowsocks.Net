using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Shadowsocks.Protocol.ServersControllers;
using Shadowsocks.Protocol.UdpClients;
using Socks5.Enums;
using Socks5.Models;
using Socks5.Utils;
using System.Buffers;
using System.Net.Sockets;

namespace Shadowsocks.Protocol.LocalUdpServices;

public class Socks5UdpService : ILocalUdpService
{
	private readonly ILogger _logger;
	private readonly IServersController _serversController;
	private readonly IMemoryCache _cache;

	public TimeSpan? SlidingExpiration
	{
		get => _cacheOptions.SlidingExpiration;
		set => _cacheOptions.SlidingExpiration = value;
	}

	private readonly MemoryCacheEntryOptions _cacheOptions = new MemoryCacheEntryOptions()
		.SetSlidingExpiration(TimeSpan.FromMinutes(1))
		.RegisterPostEvictionCallback(
			(key, value, reason, state) =>
			{
				if (value is IDisposable disposable)
				{
					disposable.Dispose();
				}
			}
		);

	public Socks5UdpService(
		ILogger<Socks5UdpService> logger,
		IServersController serversController,
		IMemoryCache cache)
	{
		_logger = logger;
		_serversController = serversController;
		_cache = cache;
	}

	public bool IsHandle(ReadOnlyMemory<byte> buffer)
	{
		try
		{
			Socks5UdpReceivePacket socks5UdpPacket = Unpack.Udp(buffer);
			return socks5UdpPacket.Fragment is 0x00;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public async ValueTask HandleAsync(UdpReceiveResult receiveResult, UdpClient incoming, CancellationToken cancellationToken = default)
	{
		try
		{
			Socks5UdpReceivePacket socks5UdpPacket = Unpack.Udp(receiveResult.Buffer);

			string target = socks5UdpPacket.Type switch
			{
				AddressType.Domain => socks5UdpPacket.Domain!,
				_ => socks5UdpPacket.Address!.ToString()
			};

			if (!_cache.TryGetValue(receiveResult.RemoteEndPoint, out IUdpClient client))
			{
				client = await _serversController.GetServerUdpAsync(target);
				if (client is ConnectionRefusedUdpClient)
				{
					_logger.LogInformation(@"Udp Send to {Target} Refused", target);
					return;
				}

				_cache.Set(receiveResult.RemoteEndPoint, client, _cacheOptions);
			}

			_logger.LogInformation(@"Udp Send to {Target} via {Client}", target, client);
			Memory<byte> sendBuffer = receiveResult.Buffer.AsMemory(3); //TODO Only support ss now
			await client.SendAsync(sendBuffer, cancellationToken);

			byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(0x10000);
			try
			{
				int receiveLength = await client.ReceiveAsync(receiveBuffer.AsMemory(3), cancellationToken);
				receiveBuffer.AsSpan(0, 3).Clear();

				await incoming.Client.SendToAsync(
					receiveBuffer.AsMemory(0, 3 + receiveLength),
					SocketFlags.None,
					receiveResult.RemoteEndPoint,
					cancellationToken
				);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(receiveBuffer);
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, @"SOCKS5 Udp handle error");
		}
	}
}
