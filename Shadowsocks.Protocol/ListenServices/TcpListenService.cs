using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Pipelines.Extensions;
using Shadowsocks.Protocol.LocalTcpServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
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
		private static readonly StreamPipeReaderOptions LocalPipeReaderOptions = new(bufferSize: FirstBufferSize);

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
			var remoteEndPoint = rec.Client.RemoteEndPoint;
			try
			{
				var pipe = rec.GetStream().AsDuplexPipe(LocalPipeReaderOptions);
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

					// In every service.HandleAsync, first ReadResult.IsCanceled always true
					await service.HandleAsync(pipe, token);
				}
				finally
				{
					await pipe.Input.CompleteAsync();
					await pipe.Output.CompleteAsync();
				}
			}
			catch (ObjectDisposedException)
			{

			}
			catch (Exception ex)
			{
				_logger.LogError(ex, @"{0} Handle Error", LoggerHeader);
			}
			finally
			{
				rec.Dispose();
				// rec.Client has already been disposed when pipe completed
				_logger.LogInformation(@"{0} {1} disconnected", LoggerHeader, remoteEndPoint);
			}
		}

		public void Stop()
		{
			_cts.Cancel();
			_tcpListener.Stop();
		}
	}
}
