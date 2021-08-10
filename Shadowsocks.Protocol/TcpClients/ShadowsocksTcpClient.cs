using Microsoft;
using Microsoft.Extensions.Logging;
using Pipelines.Extensions;
using Shadowsocks.Protocol.Models;
using System;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.TcpClients
{
	public sealed class ShadowsocksTcpClient : IPipeClient
	{
		private TcpClient? _client;

		public IDuplexPipe? Pipe { get; private set; }

		private readonly ILogger _logger;
		private readonly ShadowsocksServerInfo _serverInfo;

		private const string LogHeader = @"[ShadowsocksTcpClient]";

		public ShadowsocksTcpClient(ILogger logger, ShadowsocksServerInfo serverInfo)
		{
			_logger = logger;
			_serverInfo = serverInfo;
		}

		public async ValueTask<bool> TryConnectAsync(CancellationToken token)
		{
			try
			{
				Requires.NotNullAllowStructs(_serverInfo.Address, nameof(_serverInfo.Address));

				_client = new TcpClient { NoDelay = true };
				await _client.ConnectAsync(_serverInfo.Address, _serverInfo.Port, token);

				Pipe = _client.GetStream().AsDuplexPipe().AsShadowsocksPipe(_serverInfo);

				_logger.LogDebug(@"{0} TryConnect success", LogHeader);
				return true;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, @"{0} TryConnect error", LogHeader);
				return false;
			}
		}

		public override string? ToString()
		{
			return _serverInfo.ToString();
		}

		public async ValueTask DisposeAsync()
		{
			_client?.Dispose();
			if (Pipe is not null)
			{
				await Pipe.Input.CompleteAsync();
				await Pipe.Output.CompleteAsync();
			}
		}
	}
}
