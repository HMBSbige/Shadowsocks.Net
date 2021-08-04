using Microsoft.Extensions.Logging;
using Pipelines.Extensions;
using Shadowsocks.Protocol.ServersControllers;
using Shadowsocks.Protocol.TcpClients;
using Socks5.Enums;
using Socks5.Models;
using Socks5.Servers;
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.LocalTcpServices
{
	public class Socks5Service : ILocalTcpService
	{
		private readonly ILogger _logger;
		private readonly IServersController _serversController;

		public IPEndPoint? BindEndPoint { get; set; }

		public NetworkCredential? Credential { get; set; }

		public Socks5Service(
			ILogger<Socks5Service> logger,
			IServersController serversController)
		{
			_logger = logger;
			_serversController = serversController;
		}

		public bool IsHandle(ReadOnlySequence<byte> buffer)
		{
			return Socks5ServerConnection.IsClientHeader(buffer);
		}

		public async ValueTask HandleAsync(IDuplexPipe pipe, CancellationToken token = default)
		{
			if (BindEndPoint is null)
			{
				throw new ArgumentNullException(nameof(BindEndPoint));
			}

			var socks5 = new Socks5ServerConnection(pipe, Credential);

			await socks5.AcceptClientAsync(token);

			var outType = BindEndPoint.AddressFamily == AddressFamily.InterNetwork ? AddressType.IPv4 : AddressType.IPv6;

			switch (socks5.Command)
			{
				case Command.Connect:
				{
					_logger.LogDebug(@"SOCKS5 Connect");

					var target = socks5.Target.Type switch
					{
						AddressType.Domain => socks5.Target.Domain!,
						_ => socks5.Target.Address!.ToString()
					};

					await using var client = await _serversController.GetServerAsync(target);

					_logger.LogInformation($@"SOCKS5 Connect to {target} via {client}");

					var bound = new ServerBound
					{
						Type = outType,
						Address = BindEndPoint.Address,
						Port = IPEndPoint.MinPort
					};
					await socks5.SendReplyAsync(Socks5Reply.Succeeded, bound, token);

					if (client.Pipe is null)
					{
						throw new InvalidOperationException(@"You should TryConnect successfully first!");
					}

					await client.Pipe.Output.SendShadowsocksHeaderAsync(target, socks5.Target.Port, token);

					await client.Pipe.LinkToAsync(pipe, token);

					break;
				}
				case Command.Bind:
				{
					_logger.LogDebug(@"SOCKS5 Bind");

					var bound = new ServerBound
					{
						Type = outType,
						Address = BindEndPoint.Address,
						Port = IPEndPoint.MinPort
					};
					await socks5.SendReplyAsync(Socks5Reply.CommandNotSupported, bound, token);
					break;
				}
				case Command.UdpAssociate:
				{
					_logger.LogDebug(@"SOCKS5 UdpAssociate");

					var bound = new ServerBound
					{
						Type = outType,
						Address = BindEndPoint.Address,
						Port = (ushort)BindEndPoint.Port
					};
					await socks5.SendReplyAsync(Socks5Reply.Succeeded, bound, token);
					//TODO
					break;
				}
				default:
				{
					var bound = new ServerBound
					{
						Type = outType,
						Address = BindEndPoint.Address,
						Port = IPEndPoint.MinPort
					};
					await socks5.SendReplyAsync(Socks5Reply.GeneralFailure, bound, token);
					break;
				}
			}

		}
	}
}
