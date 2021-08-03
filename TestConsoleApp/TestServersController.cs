using Microsoft.Extensions.Logging;
using Shadowsocks.Crypto;
using Shadowsocks.Protocol.Models;
using Shadowsocks.Protocol.ServersControllers;
using Shadowsocks.Protocol.TcpClients;
using Shadowsocks.Protocol.UdpClients;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestConsoleApp
{
	public class TestServersController : IServersController
	{
		private readonly ILogger _logger;

		public TestServersController(ILogger<TestServersController> logger)
		{
			_logger = logger;
		}

		public async ValueTask<IPipeClient> GetServerAsync(string target)
		{
			var info = new ShadowsocksServerInfo
			{
				Address = @"",
				Port = 0,
				Method = ShadowsocksCrypto.Aes128GcmMethod,
				Password = @"",
				Remark = @""
			};

			IPipeClient client = new ShadowsocksTcpClient(_logger, info);

			var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

			if (!await client.TryConnectAsync(cts.Token))
			{
				throw new Exception($@"Connect to {info} Error");
			}

			return client;
		}

		public ValueTask<IUdpClient> GetServerUdpAsync(string target)
		{
			var info = new ShadowsocksServerInfo
			{
				Address = @"",
				Port = 0,
				Method = ShadowsocksCrypto.Aes128GcmMethod,
				Password = @"",
				Remark = @""
			};

			IUdpClient client = new ShadowsocksUdpClient(_logger, info);

			return ValueTask.FromResult(client);
		}
	}
}
