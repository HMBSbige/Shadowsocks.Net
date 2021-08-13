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
		public TcpListener TCPListener { get; }
		private readonly IEnumerable<ILocalTcpService> _services;

		private readonly CancellationTokenSource _cts;

		private const string LoggerHeader = @"[TcpListenService]";
		private const int FirstBufferSize = 8192;
		private static readonly StreamPipeReaderOptions LocalPipeReaderOptions = new(bufferSize: FirstBufferSize);

		public TcpListenService(ILogger<TcpListenService> logger, IPEndPoint local, IEnumerable<ILocalTcpService> services)
		{
			_logger = logger;
			TCPListener = new TcpListener(local);
			_services = services;

			_cts = new CancellationTokenSource();
		}

		public async ValueTask StartAsync()
		{
			try
			{
				TCPListener.Start();
				_logger.LogInformation(@"{0} {1} Start", LoggerHeader, TCPListener.LocalEndpoint);

				while (!_cts.IsCancellationRequested)
				{
					var rec = await TCPListener.AcceptTcpClientAsync();
					rec.NoDelay = true;

					_logger.LogInformation(@"{0} {1} => {2}", LoggerHeader, rec.Client.RemoteEndPoint, rec.Client.LocalEndPoint);
					HandleAsync(rec, _cts.Token).Forget();
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, @"{0} {1} Stop!", LoggerHeader, TCPListener.LocalEndpoint);
				Stop();
			}
		}

		private async ValueTask HandleAsync(TcpClient rec, CancellationToken token)
		{
			var remoteEndPoint = rec.Client.RemoteEndPoint;
			try
			{
				var pipe = rec.GetStream().AsDuplexPipe(LocalPipeReaderOptions);
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
			catch (ObjectDisposedException)
			{

			}
			catch (IOException ex) when (ex.InnerException is SocketException)
			{

			}
			catch (OperationCanceledException)
			{

			}
			catch (Exception ex)
			{
				_logger.LogError(ex, @"{0} Handle Error", LoggerHeader);
			}
			finally
			{
				rec.Dispose();
				_logger.LogInformation(@"{0} {1} disconnected", LoggerHeader, remoteEndPoint);
			}
		}

		public void Stop()
		{
			try
			{
				TCPListener.Stop();
			}
			finally
			{
				_cts.Cancel();
			}
		}
	}
}
