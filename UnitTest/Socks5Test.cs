using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.Threading;
using Socks5.Models;
using Socks5.Servers;
using Socks5.Utils;
using System.Net;
using System.Threading.Tasks;

namespace UnitTest
{
	[TestClass]
	public class Socks5Test
	{
		[TestMethod]
		public async Task ConnectTestAsync()
		{
			var serverEndpoint = new IPEndPoint(IPAddress.Loopback, 0);
			var userPass = new UsernamePassword
			{
				UserName = @"114514！",
				Password = @"1919810￥"
			};
			var server = new Socks5Server(serverEndpoint, userPass);
			server.StartAsync().Forget();
			try
			{
				var port = (ushort)((IPEndPoint)server.TcpListener.LocalEndpoint).Port;
				var option = new Socks5CreateOption
				{
					Address = IPAddress.Loopback,
					Port = port,
					UsernamePassword = userPass
				};
				Assert.IsTrue(await Socks5TestUtils.Socks5ConnectAsync(option));
			}
			finally
			{
				server.Stop();
			}
		}

		[TestMethod]
		public async Task UdpAssociateTestAsync()
		{
			var option = new Socks5CreateOption
			{
				Address = IPAddress.Loopback,
				Port = 23333
			};
			Assert.IsTrue(await Socks5TestUtils.Socks5UdpAssociateAsync(option));
		}
	}
}
