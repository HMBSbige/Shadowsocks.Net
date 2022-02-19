using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Pipelines.Extensions;
using Shadowsocks.Protocol.LocalTcpServices;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using static Shadowsocks.Protocol.ShadowsocksProtocolConstants;

namespace Shadowsocks.Protocol.ListenServices;

public class TcpListenService : IListenService
{
	public TcpListener TCPListener { get; }

	private readonly ILogger<TcpListenService> _logger;
	private readonly IEnumerable<ILocalTcpService> _services;

	private readonly CancellationTokenSource _cts;

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
			_logger.LogInformation(@"{Local} Start", TCPListener.LocalEndpoint);

			while (!_cts.IsCancellationRequested)
			{
				Socket socket = await TCPListener.AcceptSocketAsync();
				socket.NoDelay = true;

				_logger.LogInformation(@"{Remote} => {Local}", socket.RemoteEndPoint, socket.LocalEndPoint);
				HandleAsync(socket, _cts.Token).Forget();
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, @"{Local} Stop!", TCPListener.LocalEndpoint);
			Stop();
		}
	}

	private async Task HandleAsync(Socket socket, CancellationToken token)
	{
		EndPoint? remoteEndPoint = socket.RemoteEndPoint;
		try
		{
			IDuplexPipe pipe = socket.AsDuplexPipe(SocketPipeReaderOptions, SocketPipeWriterOptions);
			ReadResult result = await pipe.Input.ReadAsync(token);
			ReadOnlySequence<byte> buffer = result.Buffer;

			ILocalTcpService? service = _services.FirstOrDefault(tcpService => tcpService.IsHandle(buffer));

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
			_logger.LogError(ex, @"Handle Error");
		}
		finally
		{
			socket.FullClose();
			_logger.LogInformation(@"{Remote} disconnected", remoteEndPoint);
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
