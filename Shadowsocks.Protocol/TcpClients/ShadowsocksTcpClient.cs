using Microsoft;
using Pipelines.Extensions;
using Shadowsocks.Protocol.Models;
using System.IO.Pipelines;
using System.Net.Sockets;
using static Shadowsocks.Protocol.ShadowsocksProtocolConstants;

namespace Shadowsocks.Protocol.TcpClients;

public sealed class ShadowsocksTcpClient : IPipeClient
{
	private TcpClient? _client;

	private IDuplexPipe? _pipe;

	private readonly ShadowsocksServerInfo _serverInfo;

	public ShadowsocksTcpClient(ShadowsocksServerInfo serverInfo)
	{
		_serverInfo = serverInfo;
	}

	public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
	{
		Verify.Operation(_client is null || _client.Connected, @"Client has already connected!");
		Requires.NotNullAllowStructs(_serverInfo.Address, nameof(_serverInfo.Address));
		Requires.NotDefault(_serverInfo.Port, nameof(_serverInfo.Port));

		_client = new TcpClient { NoDelay = true };
		await _client.ConnectAsync(_serverInfo.Address, _serverInfo.Port, cancellationToken);
	}

	public IDuplexPipe GetPipe(string targetAddress, ushort targetPort)
	{
		Verify.Operation(_client is not null && _client.Connected, @"You must connect to the server first!");

		return _pipe ??= _client.Client
			.AsDuplexPipe(SocketPipeReaderOptions, SocketPipeWriterOptions)
			.AsShadowsocksPipe(
				_serverInfo,
				targetAddress, targetPort,
				DefaultPipeOptions,
				DefaultPipeOptions
			);
	}

	public override string? ToString()
	{
		return _serverInfo.ToString();
	}

	public void Dispose()
	{
		_client?.Dispose();
	}
}
