using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Pipelines.Extensions;
using Shadowsocks.Protocol.LocalTcpServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.ListenServices
{
	public class TcpListenService : IListenService
	{
		private readonly ILogger<TcpListenService> _logger;
		private readonly TcpListener _tcpListener;
		private readonly IEnumerable<ILocalTcpService> _services;

		private readonly CancellationTokenSource _cts;

		private const string LoggerHeader = @"[TcpListenService]";
		private const int FirstBufferSize = 8192;

		public TcpListenService(ILogger<TcpListenService> logger, IPEndPoint local, IEnumerable<ILocalTcpService> services)
		{
			_logger = logger;
			_tcpListener = new TcpListener(local);
			_services = services;

			_cts = new CancellationTokenSource();
		}

		public async ValueTask StartAsync()
		{
			try
			{
				_tcpListener.Start();
				_logger.LogInformation(@"{0} {1} Start", LoggerHeader, _tcpListener.LocalEndpoint);

				while (!_cts.IsCancellationRequested)
				{
					var rec = await _tcpListener.AcceptTcpClientAsync();
					rec.NoDelay = true;

					_logger.LogInformation(@"{0} {1} => {2}", LoggerHeader, rec.Client.RemoteEndPoint, rec.Client.LocalEndPoint);
					HandleAsync(rec, _cts.Token).Forget();
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, @"{0} {1} Stop!", LoggerHeader, _tcpListener.LocalEndpoint);
				Stop();
			}
		}

		private async ValueTask HandleAsync(TcpClient rec, CancellationToken token)
		{
			try
			{
				var pipe = rec.GetStream().AsDuplexPipe(FirstBufferSize, cancellationToken: token);
				try
				{
					var result = await pipe.Input.ReadAsync(token);
					var buffer = result.Buffer;

					var service = _services.FirstOrDefault(tcpService => tcpService.IsHandle(buffer));

					if (service is null)
					{
						throw new InvalidDataException(@"Cannot handle incoming pipe.");
					}

					pipe.Input.AdvanceTo(buffer.Start, buffer.End);
					pipe.Input.CancelPendingRead();

					await service.HandleAsync(pipe, token);
				}
				finally
				{
					await pipe.Output.CompleteAsync();
					await pipe.Input.CompleteAsync();
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, @"{0} Handle Error", LoggerHeader);
			}
			finally
			{
				_logger.LogInformation(@"{0} {1} disconnected", LoggerHeader, rec.Client.RemoteEndPoint);
				rec.Dispose();
			}
		}

		public void Stop()
		{
			_cts.Cancel();
			_tcpListener.Stop();
		}
	}
}
