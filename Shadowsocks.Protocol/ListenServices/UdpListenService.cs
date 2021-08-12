using Microsoft.Extensions.Logging;
using Shadowsocks.Protocol.LocalUdpServices;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.ListenServices
{
	//TODO Remake
	public class UdpListenService : IListenService
	{
		private readonly ILogger<UdpListenService> _logger;
		private readonly UdpClient _udpListener;
		private readonly IEnumerable<ILocalUdpService> _services;

		private readonly CancellationTokenSource _cts;

		private const string LoggerHeader = @"[UdpListenService]";

		public UdpListenService(ILogger<UdpListenService> logger, IPEndPoint local, IEnumerable<ILocalUdpService> services)
		{
			_logger = logger;
			_udpListener = new UdpClient(local);
			_services = services;

			_cts = new CancellationTokenSource();
		}

		public async ValueTask StartAsync()
		{
			try
			{
				_logger.LogInformation(@"{0} {1} Start", LoggerHeader, _udpListener.Client.LocalEndPoint);
				while (!_cts.IsCancellationRequested)
				{
					var message = await _udpListener.ReceiveAsync();
#if DEBUG
					_logger.LogDebug(@"{0}: {1} bytes {2} => {2}", LoggerHeader, message.Buffer.Length, message.RemoteEndPoint, _udpListener.Client.LocalEndPoint);
#endif
					_ = HandleAsync(message, _cts.Token);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, @"{0} Stop!", _udpListener.Client.LocalEndPoint);
				Stop();
			}
		}

		private async ValueTask HandleAsync(UdpReceiveResult result, CancellationToken token)
		{
			try
			{
				foreach (var service in _services)
				{
					token.ThrowIfCancellationRequested();
					if (await service.IsHandleAsync(result, _udpListener))
					{
						return;
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, @"{0} Handle Error", LoggerHeader);
			}
		}

		public void Stop()
		{
			_cts.Cancel();
			_udpListener.Dispose();

			foreach (var service in _services)
			{
				try
				{
					service.Stop();
				}
				catch
				{
					// ignored
				}
			}
		}
	}
}
