using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Pipelines.Extensions;
using Pipelines.Extensions.SocketPipe;
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
		public TcpListener TCPListener { get; }

		private readonly ILogger<TcpListenService> _logger;
		private readonly IEnumerable<ILocalTcpService> _services;

		private readonly CancellationTokenSource _cts;

		private const string LoggerHeader = @"[TcpListenService]";
		private const int FirstBufferSize = 8192;
		private static readonly SocketPipeReaderOptions LocalPipeReaderOptions = new(sizeHint: FirstBufferSize);

		public TcpListenService(ILogger<TcpListenService> logger, IPEndPoint local, IEnumerable<ILocalTcpService> services)
		{
			_logger = logger;
			_services = services;

			TCPListener = new TcpListener(local);
			_cts = new CancellationTokenSource();
		}

		public async ValueTask StartAsync()
		{
			try
			{
				TCPListener.Start();
				_logger.LogInformation(@"{LoggerHeader} {Local} Start", LoggerHeader, TCPListener.LocalEndpoint);

				while (!_cts.IsCancellationRequested)
				{
					var socket = await TCPListener.AcceptSocketAsync();
					socket.NoDelay = true;

					_logger.LogInformation(@"{LoggerHeader} {Remote} => {Local}", LoggerHeader, socket.RemoteEndPoint, socket.LocalEndPoint);
					HandleAsync(socket, _cts.Token).Forget();
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, @"{LoggerHeader} {Local} Stop!", LoggerHeader, TCPListener.LocalEndpoint);
				Stop();
			}
		}

		private async Task HandleAsync(Socket socket, CancellationToken token)
		{
			var remoteEndPoint = socket.RemoteEndPoint;
			try
			{
				var pipe = socket.AsDuplexPipe(LocalPipeReaderOptions);
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
				_logger.LogError(ex, @"{LoggerHeader} Handle Error", LoggerHeader);
			}
			finally
			{
				socket.FullClose();
				_logger.LogInformation(@"{LoggerHeader} {Remote} disconnected", LoggerHeader, remoteEndPoint);
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
