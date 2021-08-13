using HttpProxy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.Threading;
using Socks5.Models;
using Socks5.Servers;
using Socks5.Utils;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace UnitTest
{
	[TestClass]
	public class HttpTest
	{
		[TestMethod]
		public async Task TestAsync()
		{
			var serverEndpoint = new IPEndPoint(IPAddress.Loopback, 0);
			var userPass = new UsernamePassword
			{
				UserName = @"114514！",
				Password = @"1919810￥"
			};
			var server = new SimpleSocks5Server(serverEndpoint, userPass);
			server.StartAsync().Forget();
			try
			{
				var port = (ushort)((IPEndPoint)server.TcpListener.LocalEndpoint).Port;
				var socks5CreateOption = new Socks5CreateOption
				{
					Address = IPAddress.Loopback,
					Port = port,
					UsernamePassword = userPass
				};
				var httpServer = new HttpSocks5Service(serverEndpoint, new HttpToSocks5(), socks5CreateOption);
				httpServer.StartAsync().Forget();
				try
				{
					var httpAddress = ((IPEndPoint)httpServer.TcpListener.LocalEndpoint).Address;
					var httpPort = (ushort)((IPEndPoint)httpServer.TcpListener.LocalEndpoint).Port;
					var handler = new SocketsHttpHandler
					{
						UseProxy = true,
						Proxy = new WebProxy(httpAddress.ToString(), httpPort)
					};
					var httpClient = new HttpClient(handler);

					var httpsStr = await httpClient.GetStringAsync(@"https://api.ip.sb/ip");
					Assert.IsFalse(string.IsNullOrWhiteSpace(httpsStr));

					var httpChunkStr = await httpClient.GetStringAsync(@"http://api.ip.sb/ip");
					Assert.IsFalse(string.IsNullOrWhiteSpace(httpChunkStr));

					Assert.AreEqual(httpsStr, httpChunkStr);

					httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(@"curl/7.55.1");
					var httpStr = await httpClient.GetStringAsync(@"http://ip.sb");
					Assert.IsFalse(string.IsNullOrWhiteSpace(httpStr));

					Assert.AreEqual(httpChunkStr, httpStr);

					socks5CreateOption = new Socks5CreateOption
					{
						Address = httpAddress,
						Port = httpPort,
						UsernamePassword = userPass
					};
					Assert.IsTrue(await Socks5TestUtils.Socks5ConnectAsync(socks5CreateOption));
				}
				finally
				{
					httpServer.Stop();
				}
			}
			finally
			{
				server.Stop();
			}
		}

		[TestMethod]
		public void IsHttpHeaderTest()
		{
			var sequence0 = TestUtils.GetMultiSegmentSequence(
				Encoding.ASCII.GetBytes("GET / HTTP/1.1"),
				Encoding.ASCII.GetBytes("\r\nHost: ip.sb\r\n"),
				Encoding.ASCII.GetBytes("User-Agent: curl/7.55.1\r\n"),
				Encoding.ASCII.GetBytes("\r\n")
			);
			Assert.IsTrue(HttpUtils.IsHttpHeader(sequence0));

			var sequence1 = TestUtils.GetMultiSegmentSequence(
				Encoding.ASCII.GetBytes("GET / HTTP/1.1"),
				Encoding.ASCII.GetBytes("\r\nHost: ip.sb\r\n"),
				Encoding.ASCII.GetBytes("User-Agent: curl/7.55.1\r\n")
			);
			Assert.IsFalse(HttpUtils.IsHttpHeader(sequence1));

			var sequence2 = TestUtils.GetMultiSegmentSequence(
				Encoding.ASCII.GetBytes("\r\n"),
				Encoding.ASCII.GetBytes("\r\n")
			);
			Assert.IsFalse(HttpUtils.IsHttpHeader(sequence2));

			var sequence3 = TestUtils.GetMultiSegmentSequence(
				Encoding.ASCII.GetBytes("GET HTTP/1.1"),
				Encoding.ASCII.GetBytes("\r\nHost: ip.sb\r\n"),
				Encoding.ASCII.GetBytes("User-Agent: curl/7.55.1\r\n"),
				Encoding.ASCII.GetBytes("\r\n")
			);
			Assert.IsFalse(HttpUtils.IsHttpHeader(sequence3));
		}
	}
}
