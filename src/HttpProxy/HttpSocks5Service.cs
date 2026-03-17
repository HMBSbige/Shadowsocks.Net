using Pipelines.Extensions;
using Socks5.Models;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace HttpProxy;

/// <summary>
/// Listens for incoming TCP connections and forwards HTTP or SOCKS5 traffic through a SOCKS5 proxy.
/// </summary>
/// <param name="bindEndPoint">The local endpoint to bind the TCP listener to.</param>
/// <param name="httpToSocks5">The handler that rewrites and forwards HTTP requests through SOCKS5.</param>
/// <param name="socks5CreateOption">Options for creating SOCKS5 connections.</param>
public class HttpSocks5Service(IPEndPoint bindEndPoint, HttpToSocks5 httpToSocks5, Socks5CreateOption socks5CreateOption)
{
	/// <summary>
	/// Gets the underlying <see cref="System.Net.Sockets.TcpListener"/> used to accept connections.
	/// </summary>
	public TcpListener TcpListener { get; } = new(bindEndPoint);

	private readonly IPAddress _socks5Address = socks5CreateOption.Address ?? throw new ArgumentNullException(nameof(socks5CreateOption));
	private readonly CancellationTokenSource _cts = new();

	/// <summary>
	/// Starts accepting and forwarding connections. Runs until <see cref="Stop"/> is called.
	/// </summary>
	public async ValueTask StartAsync()
	{
		try
		{
			TcpListener.Start();

			while (!_cts.IsCancellationRequested)
			{
				Socket socket = await TcpListener.AcceptSocketAsync();
				socket.NoDelay = true;
				_ = HandleAsync(socket, _cts.Token);
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
				await socks5.ConnectAsync(_socks5Address, socks5CreateOption.Port, token);
				IDuplexPipe socks5Pipe = socks5.Client.AsDuplexPipe();

				await socks5Pipe.Output.WriteAsync(buffer, token);
				pipe.Input.AdvanceTo(buffer.End);

				await socks5Pipe.LinkToAsync(pipe, token);
			}
			else
			{
				pipe.Input.AdvanceTo(buffer.Start, buffer.End);
				pipe.Input.CancelPendingRead();

				await httpToSocks5.ForwardToSocks5Async(pipe, socks5CreateOption, token);
			}
		}
		finally
		{
			socket.FullClose();
		}

		return;

		static bool IsSocks5Header(ReadOnlySequence<byte> buffer)
		{
			SequenceReader<byte> reader = new(buffer);
			return reader.TryRead(out byte ver) && ver is 0x05;
		}
	}

	/// <summary>
	/// Stops the listener and cancels all pending operations.
	/// </summary>
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
