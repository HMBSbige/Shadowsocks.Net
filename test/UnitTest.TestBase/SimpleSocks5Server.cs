using Pipelines.Extensions;
using Socks5.Enums;
using Socks5.Models;
using Socks5.Servers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace UnitTest.TestBase;

public sealed class SimpleSocks5Server(IPEndPoint bindEndPoint, UsernamePassword? credential = null)
{
	public TcpListener TcpListener { get; } = new(bindEndPoint);

	public ServerBound ReplyTcpBound { get; set; } = new()
	{
		Type = AddressType.IPv4,
		Address = IPAddress.Any,
		Domain = default,
		Port = IPEndPoint.MinPort,
	};

	private readonly CancellationTokenSource _cts = new();

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

	private async ValueTask HandleAsync(Socket socket, CancellationToken cancellationToken)
	{
		try
		{
			IDuplexPipe pipe = socket.AsDuplexPipe();
			Socks5ServerConnection service = new(pipe, credential);
			await service.AcceptClientAsync(cancellationToken);

			switch (service.Command)
			{
				case Command.Connect:
				{
					using TcpClient tcp = new();

					if (service.Target.Type is AddressType.Domain)
					{
						Debug.Assert(service.Target.Domain is not null);
						await tcp.ConnectAsync(service.Target.Domain, service.Target.Port, cancellationToken);
					}
					else
					{
						Debug.Assert(service.Target.Address is not null);
						await tcp.ConnectAsync(service.Target.Address, service.Target.Port, cancellationToken);
					}

					await service.SendReplyAsync(Socks5Reply.Succeeded, ReplyTcpBound, cancellationToken);

					IDuplexPipe tcpPipe = tcp.Client.AsDuplexPipe();

					// Relay with TCP half-close: when one direction ends,
					// shutdown the other socket's Send so the peer sees EOF.
					Task a = CopyThenShutdownAsync(tcpPipe.Input, pipe.Output, socket, cancellationToken);
					Task b = CopyThenShutdownAsync(pipe.Input, tcpPipe.Output, tcp.Client, cancellationToken);
					await Task.WhenAll(a, b);

					break;
				}
				case Command.Bind:
				{
					await service.SendReplyAsync(Socks5Reply.CommandNotSupported, ReplyTcpBound, cancellationToken);
					break;
				}
				case Command.UdpAssociate:
				{
					IPEndPoint remote = (IPEndPoint)socket.RemoteEndPoint!;
					IPEndPoint local = new(((IPEndPoint)TcpListener.LocalEndpoint).Address, IPEndPoint.MinPort);
					using SimpleSocks5UdpServer udpServer = new(local, remote);
					_ = udpServer.StartAsync();

					ServerBound replyUdpBound = new()
					{
						Type = AddressType.IPv4,
						Address = local.Address,
						Domain = default,
						Port = (ushort)((IPEndPoint)udpServer.UdpListener.Client.LocalEndPoint!).Port,
					};

					await service.SendReplyAsync(Socks5Reply.Succeeded, replyUdpBound, cancellationToken);

					// wait remote close
					ReadResult result = await pipe.Input.ReadAsync(cancellationToken);
					Debug.Assert(result.IsCompleted);

					break;
				}
				default:
				{
					await service.SendReplyAsync(Socks5Reply.GeneralFailure, ReplyTcpBound, cancellationToken);
					break;
				}
			}
		}
		finally
		{
			socket.FullClose();
		}
	}

	private static async Task CopyThenShutdownAsync(PipeReader source, PipeWriter destination, Socket socketToShutdown, CancellationToken cancellationToken)
	{
		try
		{
			await source.CopyToAsync(destination, cancellationToken);
		}
		finally
		{
			socketToShutdown.Shutdown(SocketShutdown.Send);
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
