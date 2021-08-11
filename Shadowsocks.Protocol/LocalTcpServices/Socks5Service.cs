using Microsoft;
using Microsoft.Extensions.Logging;
using Pipelines.Extensions;
using Shadowsocks.Protocol.ServersControllers;
using Socks5.Enums;
using Socks5.Models;
using Socks5.Servers;
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

		public Socks5CreateOption? Socks5CreateOption { get; set; }

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
			Verify.Operation(Socks5CreateOption is not null, @"You must set {0}", nameof(Socks5CreateOption));
			Verify.Operation(Socks5CreateOption.Address is not null, @"You must set socks5 address");

			var socks5 = new Socks5ServerConnection(pipe, Socks5CreateOption.UsernamePassword);

			await socks5.AcceptClientAsync(token);

			var outType = Socks5CreateOption.Address.AddressFamily is AddressFamily.InterNetwork ? AddressType.IPv4 : AddressType.IPv6;

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
						Address = Socks5CreateOption.Address,
						Port = IPEndPoint.MinPort
					};
					await socks5.SendReplyAsync(Socks5Reply.Succeeded, bound, token);

					var clientPipe = client.GetPipe(target, socks5.Target.Port);

					await clientPipe.LinkToAsync(pipe, token);

					break;
				}
				case Command.Bind:
				{
					_logger.LogDebug(@"SOCKS5 Bind");

					var bound = new ServerBound
					{
						Type = outType,
						Address = Socks5CreateOption.Address,
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
						Address = Socks5CreateOption.Address,
						Port = Socks5CreateOption.Port
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
						Address = Socks5CreateOption.Address,
						Port = IPEndPoint.MinPort
					};
					await socks5.SendReplyAsync(Socks5Reply.GeneralFailure, bound, token);
					break;
				}
			}

		}
	}
}
