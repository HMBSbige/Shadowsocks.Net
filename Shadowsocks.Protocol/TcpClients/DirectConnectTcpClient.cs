using Microsoft;
using Pipelines.Extensions;
using System.IO.Pipelines;
using System.Net.Sockets;
using static Shadowsocks.Protocol.ShadowsocksProtocolConstants;

namespace Shadowsocks.Protocol.TcpClients;

public class DirectConnectTcpClient : IPipeClient
{
	private TcpClient? _client;

	private IDuplexPipe? _pipe;

	private readonly string _targetAddress;
	private readonly ushort _targetPort;

	public DirectConnectTcpClient(string targetAddress, ushort targetPort)
	{
		_targetAddress = targetAddress;
		_targetPort = targetPort;
	}

	public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
	{
		Verify.Operation(_client is null || !_client.Connected, @"Client has already connected!");

		_client = new TcpClient { NoDelay = true };
		await _client.ConnectAsync(_targetAddress, _targetPort, cancellationToken);
	}

	public IDuplexPipe GetPipe()
	{
		Verify.Operation(_client is not null && _client.Connected, @"You must connect to the server first!");

		return _pipe ??= _client.Client
			.AsDuplexPipe(DefaultSocketPipeReaderOptions, DefaultSocketPipeWriterOptions);
	}

	public override string ToString()
	{
		return @"DirectConnect";
	}

	public void Dispose()
	{
		_client?.Dispose();
		GC.SuppressFinalize(this);
	}
}
