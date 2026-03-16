using HttpProxy;
using Socks5.Models;
using Socks5.Servers;
using Socks5.Utils;
using System.Buffers;
using System.Net;

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
		_ = server.StartAsync();

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
			_ = httpServer.StartAsync();

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
				byte[] httpChunkBytes = await httpClient.GetByteArrayAsync(@"http://httpbin.org/stream-bytes/1024");
				Assert.AreEqual(1024, httpChunkBytes.Length);

				// HTTP Content-Length
				httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(@"curl");
				string httpStr = await httpClient.GetStringAsync(@"http://ip.sb");
				Assert.IsFalse(string.IsNullOrWhiteSpace(httpStr));

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
			"GET / HTTP/1.1"u8.ToArray(),
			"\r\nHost: ip.sb\r\n"u8.ToArray(),
			"User-Agent: curl/7.55.1\r\n"u8.ToArray(),
			"\r\n"u8.ToArray()
		);
		Assert.IsTrue(HttpUtils.IsHttpHeader(sequence0));

		ReadOnlySequence<byte> sequence1 = TestUtils.GetMultiSegmentSequence(
			"GET / HTTP/1.1"u8.ToArray(),
			"\r\nHost: ip.sb\r\n"u8.ToArray(),
			"User-Agent: curl/7.55.1\r\n"u8.ToArray()
		);
		Assert.IsFalse(HttpUtils.IsHttpHeader(sequence1));

		ReadOnlySequence<byte> sequence2 = TestUtils.GetMultiSegmentSequence(
			"\r\n"u8.ToArray(),
			"\r\n"u8.ToArray()
		);
		Assert.IsFalse(HttpUtils.IsHttpHeader(sequence2));

		ReadOnlySequence<byte> sequence3 = TestUtils.GetMultiSegmentSequence(
			"GET HTTP/1.1"u8.ToArray(),
			"\r\nHost: ip.sb\r\n"u8.ToArray(),
			"User-Agent: curl/7.55.1\r\n"u8.ToArray(),
			"\r\n"u8.ToArray()
		);
		Assert.IsFalse(HttpUtils.IsHttpHeader(sequence3));
	}
}
