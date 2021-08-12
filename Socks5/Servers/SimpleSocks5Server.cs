using Microsoft;
using Microsoft.VisualStudio.Threading;
using Pipelines.Extensions;
using Socks5.Enums;
using Socks5.Models;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Socks5.Servers
{
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

		public ServerBound ReplyUdpBound { get; set; } = new()
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
					var rec = await TcpListener.AcceptTcpClientAsync();
					rec.NoDelay = true;
					HandleAsync(rec, _cts.Token).Forget();
				}
			}
			catch (Exception)
			{
				Stop();
			}
		}

		private async ValueTask HandleAsync(TcpClient rec, CancellationToken token)
		{
			try
			{
				var pipe = rec.GetStream().AsDuplexPipe();
				var service = new Socks5ServerConnection(pipe, _credential);
				await service.AcceptClientAsync(token);

				switch (service.Command)
				{
					case Command.Connect:
					{
						using var tcp = new TcpClient();
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

						var tcpPipe = tcp.GetStream().AsDuplexPipe();

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
						await service.SendReplyAsync(Socks5Reply.Succeeded, ReplyUdpBound, token);
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
				rec.Dispose();
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
}
