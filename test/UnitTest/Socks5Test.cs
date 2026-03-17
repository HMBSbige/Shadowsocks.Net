using Socks5.Models;
using Socks5.Servers;
using Socks5.Utils;
using System.Net;

namespace UnitTest;

[Timeout(5_000)]
public class Socks5Test
{
	[Test]
	public async Task ConnectTestAsync(CancellationToken cancellationToken)
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
			await Assert.That(await Socks5TestUtils.Socks5ConnectAsync(option, cancellationToken: cancellationToken)).IsTrue();
		}
		finally
		{
			server.Stop();
		}
	}

	[Test]
	public async Task UdpAssociateTestAsync(CancellationToken cancellationToken)
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
			await Assert.That(await Socks5TestUtils.Socks5UdpAssociateAsync(option, cancellationToken: cancellationToken)).IsTrue();
		}
		finally
		{
			server.Stop();
		}
	}
}
