using Microsoft;
using Microsoft.VisualStudio.Threading;
using Pipelines.Extensions;
using Socks5.Models;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace HttpProxy;

public class HttpSocks5Service
{
	public TcpListener TcpListener { get; }

	private readonly HttpToSocks5 _httpToSocks5;
	private readonly Socks5CreateOption _socks5CreateOption;
	private readonly CancellationTokenSource _cts;

	public HttpSocks5Service(IPEndPoint bindEndPoint, HttpToSocks5 httpToSocks5, Socks5CreateOption socks5CreateOption)
	{
		Requires.NotNullAllowStructs(socks5CreateOption.Address, nameof(socks5CreateOption.Address));

		TcpListener = new TcpListener(bindEndPoint);
		_httpToSocks5 = httpToSocks5;
		_socks5CreateOption = socks5CreateOption;

		_cts = new CancellationTokenSource();
	}

	public async ValueTask StartAsync()
	{
		try
		{
			TcpListener.Start();
			while (!_cts.IsCancellationRequested)
			{
				Socket socket = await TcpListener.AcceptSocketAsync();
				socket.NoDelay = true;
				HandleAsync(socket, _cts.Token).Forget();
			}
		}
		catch (Exception)
		{
			Stop();
		}
	}

	private async Task HandleAsync(Socket socket, CancellationToken token)
	{
		try
		{
			IDuplexPipe pipe = socket.AsDuplexPipe();
			ReadResult result = await pipe.Input.ReadAsync(token);
			ReadOnlySequence<byte> buffer = result.Buffer;

			if (IsSocks5Header(buffer))
			{
				using TcpClient socks5 = new();
				await socks5.ConnectAsync(_socks5CreateOption.Address!, _socks5CreateOption.Port, token);
				IDuplexPipe socks5Pipe = socks5.Client.AsDuplexPipe();

				await socks5Pipe.Output.WriteAsync(buffer, token);
				pipe.Input.AdvanceTo(buffer.End);

				await socks5Pipe.LinkToAsync(pipe, token);
			}
			else
			{
				pipe.Input.AdvanceTo(buffer.Start, buffer.End);
				pipe.Input.CancelPendingRead();

				await _httpToSocks5.ForwardToSocks5Async(pipe, _socks5CreateOption, token);
			}
		}
		finally
		{
			socket.FullClose();
		}

		static bool IsSocks5Header(ReadOnlySequence<byte> buffer)
		{
			SequenceReader<byte> reader = new(buffer);
			return reader.TryRead(out byte ver) && ver is 0x05;
		}
	}

	public void Stop()
	{
		try
		{
			TcpListener.Stop();
		}
		finally
		{
			_cts.Cancel();
		}
	}
}
