using HttpProxy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.Threading;
using Socks5.Models;
using Socks5.Servers;
using Socks5.Utils;
using System.Buffers;
using System.Net;
using System.Text;

namespace UnitTest;

[TestClass]
public class HttpTest
{
	[TestMethod]
	public async Task TestAsync()
	{
		IPEndPoint serverEndpoint = new(IPAddress.Loopback, 0);
		UsernamePassword userPass = new()
		{
			UserName = @"114514！",
			Password = @"1919810￥"
		};
		SimpleSocks5Server server = new(serverEndpoint, userPass);
		server.StartAsync().Forget();
		try
		{
			ushort port = (ushort)((IPEndPoint)server.TcpListener.LocalEndpoint).Port;
			Socks5CreateOption socks5CreateOption = new()
			{
				Address = IPAddress.Loopback,
				Port = port,
				UsernamePassword = userPass
			};
			HttpSocks5Service httpServer = new(serverEndpoint, new HttpToSocks5(), socks5CreateOption);
			httpServer.StartAsync().Forget();
			try
			{
				IPAddress httpAddress = ((IPEndPoint)httpServer.TcpListener.LocalEndpoint).Address;
				ushort httpPort = (ushort)((IPEndPoint)httpServer.TcpListener.LocalEndpoint).Port;
				SocketsHttpHandler handler = new()
				{
					UseProxy = true,
					Proxy = new WebProxy(httpAddress.ToString(), httpPort)
				};
				HttpClient httpClient = new(handler);

				// CONNECT
				string httpsStr = await httpClient.GetStringAsync(@"https://api.ip.sb/ip");
				Assert.IsFalse(string.IsNullOrWhiteSpace(httpsStr));

				// HTTP chunk
				string httpChunkStr = await httpClient.GetStringAsync(@"http://api.ip.sb/ip");
				Assert.IsFalse(string.IsNullOrWhiteSpace(httpChunkStr));
				Assert.AreEqual(httpsStr, httpChunkStr);

				// HTTP Content-Length
				httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(@"curl/7.55.1");
				string httpStr = await httpClient.GetStringAsync(@"http://ip.sb");
				Assert.IsFalse(string.IsNullOrWhiteSpace(httpStr));
				Assert.AreEqual(httpChunkStr, httpStr);

				// HTTP no body
				HttpResponseMessage response = await httpClient.GetAsync(@"http://cp.cloudflare.com");
				Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

				// Forward to SOCKS5
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
		ReadOnlySequence<byte> sequence0 = TestUtils.GetMultiSegmentSequence(
			Encoding.ASCII.GetBytes("GET / HTTP/1.1"),
			Encoding.ASCII.GetBytes("\r\nHost: ip.sb\r\n"),
			Encoding.ASCII.GetBytes("User-Agent: curl/7.55.1\r\n"),
			Encoding.ASCII.GetBytes("\r\n")
		);
		Assert.IsTrue(HttpUtils.IsHttpHeader(sequence0));

		ReadOnlySequence<byte> sequence1 = TestUtils.GetMultiSegmentSequence(
			Encoding.ASCII.GetBytes("GET / HTTP/1.1"),
			Encoding.ASCII.GetBytes("\r\nHost: ip.sb\r\n"),
			Encoding.ASCII.GetBytes("User-Agent: curl/7.55.1\r\n")
		);
		Assert.IsFalse(HttpUtils.IsHttpHeader(sequence1));

		ReadOnlySequence<byte> sequence2 = TestUtils.GetMultiSegmentSequence(
			Encoding.ASCII.GetBytes("\r\n"),
			Encoding.ASCII.GetBytes("\r\n")
		);
		Assert.IsFalse(HttpUtils.IsHttpHeader(sequence2));

		ReadOnlySequence<byte> sequence3 = TestUtils.GetMultiSegmentSequence(
			Encoding.ASCII.GetBytes("GET HTTP/1.1"),
			Encoding.ASCII.GetBytes("\r\nHost: ip.sb\r\n"),
			Encoding.ASCII.GetBytes("User-Agent: curl/7.55.1\r\n"),
			Encoding.ASCII.GetBytes("\r\n")
		);
		Assert.IsFalse(HttpUtils.IsHttpHeader(sequence3));
	}
}
