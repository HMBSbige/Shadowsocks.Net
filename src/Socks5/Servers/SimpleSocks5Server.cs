using Microsoft;
using Microsoft.VisualStudio.Threading;
using Pipelines.Extensions;
using Socks5.Enums;
using Socks5.Models;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace Socks5.Servers;

/// <summary>
/// A simple SOCKS5 server for test use only
/// </summary>
public class SimpleSocks5Server
{
	public TcpListener TcpListener { get; }

	public ServerBound ReplyTcpBound { get; set; } = new()
	{
		Type = AddressType.IPv4,
		Address = IPAddress.Any,
		Domain = default,
		Port = IPEndPoint.MinPort,
	};

	private readonly UsernamePassword? _credential;
	private readonly CancellationTokenSource _cts;

	public SimpleSocks5Server(IPEndPoint bindEndPoint, UsernamePassword? credential = null)
	{
		_credential = credential;
		TcpListener = new TcpListener(bindEndPoint);

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

	private async ValueTask HandleAsync(Socket socket, CancellationToken token)
	{
		try
		{
			IDuplexPipe pipe = socket.AsDuplexPipe();
			Socks5ServerConnection service = new(pipe, _credential);
			await service.AcceptClientAsync(token);

			switch (service.Command)
			{
				case Command.Connect:
				{
					using TcpClient tcp = new();
					if (service.Target.Type is AddressType.Domain)
					{
						Assumes.NotNull(service.Target.Domain);
						await tcp.ConnectAsync(service.Target.Domain, service.Target.Port, token);
					}
					else
					{
						Assumes.NotNull(service.Target.Address);
						await tcp.ConnectAsync(service.Target.Address, service.Target.Port, token);
					}

					await service.SendReplyAsync(Socks5Reply.Succeeded, ReplyTcpBound, token);

					IDuplexPipe tcpPipe = tcp.Client.AsDuplexPipe();

					await tcpPipe.LinkToAsync(pipe, token);

					break;
				}
				case Command.Bind:
				{
					await service.SendReplyAsync(Socks5Reply.CommandNotSupported, ReplyTcpBound, token);
					break;
				}
				case Command.UdpAssociate:
				{
					IPEndPoint remote = (IPEndPoint)socket.RemoteEndPoint!;
					IPEndPoint local = new(((IPEndPoint)TcpListener.LocalEndpoint).Address, IPEndPoint.MinPort);
					using SimpleSocks5UdpServer udpServer = new(local, remote);
					udpServer.StartAsync().Forget();

					ServerBound replyUdpBound = new()
					{
						Type = AddressType.IPv4,
						Address = local.Address,
						Domain = default,
						Port = (ushort)((IPEndPoint)udpServer.UdpListener.Client.LocalEndPoint!).Port,
					};

					await service.SendReplyAsync(Socks5Reply.Succeeded, replyUdpBound, token);

					// wait remote close
					ReadResult result = await pipe.Input.ReadAsync(token);
					Report.IfNot(result.IsCompleted);

					break;
				}
				default:
				{
					await service.SendReplyAsync(Socks5Reply.GeneralFailure, ReplyTcpBound, token);
					break;
				}
			}
		}
		finally
		{
			socket.FullClose();
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
