using Socks5.Models;
using System.Net;
using UnitTest.TestBase;

namespace UnitTest;

[Timeout(5_000)]
public class Socks5Test
{
	[Test]
	public async Task ConnectTestAsync(CancellationToken cancellationToken)
	{
		MockHttpServer mockHttp = new();
		mockHttp.Start();
		try
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
				Socks5CreateOption option = new()
				{
					Address = IPAddress.Loopback,
					Port = port,
					UsernamePassword = userPass
				};
				await Assert.That(await Socks5TestUtils.Socks5ConnectAsync(
					option,
					target: "/status/204",
					targetHost: "localhost",
					targetPort: (ushort)mockHttp.Port,
					cancellationToken: cancellationToken
				)).IsTrue();
			}
			finally
			{
				server.Stop();
			}
		}
		finally
		{
			mockHttp.Dispose();
		}
	}

	[Test]
	public async Task UdpAssociateTestAsync(CancellationToken cancellationToken)
	{
		using MockUdpEchoServer echo = new();
		echo.Start();

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
			Socks5CreateOption option = new()
			{
				Address = IPAddress.Loopback,
				Port = port,
				UsernamePassword = userPass
			};
			await Assert.That(await Socks5TestUtils.Socks5UdpAssociateAsync(
				option,
				targetHost: IPAddress.Loopback.ToString(),
				targetPort: (ushort)echo.Port,
				cancellationToken: cancellationToken
			)).IsTrue();
		}
		finally
		{
			server.Stop();
		}
	}
}
