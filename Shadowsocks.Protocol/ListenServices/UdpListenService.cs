using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Shadowsocks.Protocol.LocalUdpServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.ListenServices
{
	public class UdpListenService : IListenService
	{
		private readonly ILogger<UdpListenService> _logger;
		public UdpClient UdpListener { get; }
		private readonly IEnumerable<ILocalUdpService> _services;

		private readonly CancellationTokenSource _cts;

		private const string LoggerHeader = @"[UdpListenService]";

		public UdpListenService(ILogger<UdpListenService> logger, IPEndPoint local, IEnumerable<ILocalUdpService> services)
		{
			_logger = logger;
			UdpListener = new UdpClient(local);
			_services = services;

			_cts = new CancellationTokenSource();
		}

		public async ValueTask StartAsync()
		{
			try
			{
				_logger.LogInformation(@"{0} {1} Start", LoggerHeader, UdpListener.Client.LocalEndPoint);
				while (!_cts.IsCancellationRequested)
				{
					//TODO .NET6.0
					var message = await UdpListener.ReceiveAsync();
#if DEBUG
					_logger.LogDebug(@"{0}: {1} bytes {2} => {2}", LoggerHeader, message.Buffer.Length, message.RemoteEndPoint, UdpListener.Client.LocalEndPoint);
#endif
					HandleAsync(message, _cts.Token).Forget();
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, @"{0} Stop!", UdpListener.Client.LocalEndPoint);
				Stop();
			}
		}

		private async ValueTask HandleAsync(UdpReceiveResult result, CancellationToken token)
		{
			try
			{
				var service = _services.FirstOrDefault(udpService => udpService.IsHandle(result.Buffer));

				if (service is null)
				{
					return;
				}

				await service.HandleAsync(result, UdpListener, token);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, @"{0} Handle Error", LoggerHeader);
			}
		}

		public void Stop()
		{
			try
			{
				UdpListener.Dispose();
			}
			finally
			{
				_cts.Cancel();
			}
		}
	}
}
