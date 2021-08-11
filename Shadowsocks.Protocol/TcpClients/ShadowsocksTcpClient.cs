using Microsoft;
using Pipelines.Extensions;
using Shadowsocks.Protocol.Models;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.TcpClients
{
	public sealed class ShadowsocksTcpClient : IPipeClient
	{
		private TcpClient? _client;

		private IDuplexPipe? _pipe;

		private readonly ShadowsocksServerInfo _serverInfo;

		public ShadowsocksTcpClient(ShadowsocksServerInfo serverInfo)
		{
			_serverInfo = serverInfo;
		}

		public async ValueTask ConnectAsync(CancellationToken token)
		{
			Requires.NotNullAllowStructs(_serverInfo.Address, nameof(_serverInfo.Address));

			_client = new TcpClient { NoDelay = true };
			await _client.ConnectAsync(_serverInfo.Address, _serverInfo.Port, token);
		}

		public IDuplexPipe GetPipe(string targetAddress, ushort targetPort)
		{
			Verify.Operation(_client is not null && _client.Connected, @"You must connect to the server first!");

			return _pipe ??= _client.GetStream().AsDuplexPipe().AsShadowsocksPipe(_serverInfo, targetAddress, targetPort);
		}

		public override string? ToString()
		{
			return _serverInfo.ToString();
		}

		public async ValueTask DisposeAsync()
		{
			_client?.Dispose();
			if (_pipe is not null)
			{
				await _pipe.Input.CompleteAsync();
				await _pipe.Output.CompleteAsync();
			}
		}
	}
}
