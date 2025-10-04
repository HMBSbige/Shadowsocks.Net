using Microsoft.VisualStudio.Threading;
using Socks5.Models;
using Socks5.Servers;
using Socks5.Utils;
using System.Net;

namespace UnitTest;

[TestClass]
public class Socks5Test
{
	[TestMethod]
	public async Task ConnectTestAsync()
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
			Socks5CreateOption option = new()
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
			Socks5CreateOption option = new()
			{
				Address = IPAddress.Loopback,
				Port = port,
				UsernamePassword = userPass
			};
			Assert.IsTrue(await Socks5TestUtils.Socks5UdpAssociateAsync(option));
		}
		finally
		{
			server.Stop();
		}
	}
}
