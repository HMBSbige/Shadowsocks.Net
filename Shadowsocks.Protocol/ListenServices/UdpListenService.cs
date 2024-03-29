using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Shadowsocks.Protocol.LocalUdpServices;
using System.Net;
using System.Net.Sockets;

namespace Shadowsocks.Protocol.ListenServices;

public class UdpListenService : IListenService
{
	public UdpClient UdpListener { get; }

	private readonly ILogger<UdpListenService> _logger;
	private readonly IPEndPoint _local;
	private readonly IEnumerable<ILocalUdpService> _services;

	private readonly CancellationTokenSource _cts;

	public UdpListenService(ILogger<UdpListenService> logger, IPEndPoint local, IEnumerable<ILocalUdpService> services)
	{
		_logger = logger;
		_local = local;
		_services = services;

		UdpListener = new UdpClient(local.AddressFamily);
		_cts = new CancellationTokenSource();
	}

	public async ValueTask StartAsync()
	{
		try
		{
			UdpListener.Client.Bind(_local);
			_logger.LogInformation(@"{Local} Start", UdpListener.Client.LocalEndPoint);

			while (true)
			{
				UdpReceiveResult message = await UdpListener.ReceiveAsync(_cts.Token);
#if DEBUG
				_logger.LogDebug(
					@"{ReceiveLength} bytes {Remote} => {Local}",
					message.Buffer.Length,
					message.RemoteEndPoint,
					UdpListener.Client.LocalEndPoint
				);
#endif
				HandleAsync(message, _cts.Token).Forget();
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, @"{Local} Stop!", UdpListener.Client.LocalEndPoint);
			Stop();
		}
	}

	private async Task HandleAsync(UdpReceiveResult result, CancellationToken token)
	{
		try
		{
			ILocalUdpService? service = _services.FirstOrDefault(udpService => udpService.IsHandle(result.Buffer));

			if (service is null)
			{
				return;
			}

			await service.HandleAsync(result, UdpListener, token);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, @"Handle Error");
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
