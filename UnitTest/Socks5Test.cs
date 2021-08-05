using Microsoft.VisualStudio.TestTools.UnitTesting;
using Socks5.Models;
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
			//TODO
			var option = new Socks5CreateOption
			{
				Address = IPAddress.Loopback,
				Port = 23333
			};
			Assert.IsTrue(await TestUtils.Socks5ConnectAsync(option));
		}

		[TestMethod]
		public async Task UdpAssociateTestAsync()
		{
			var option = new Socks5CreateOption
			{
				Address = IPAddress.Loopback,
				Port = 23333
			};
			Assert.IsTrue(await TestUtils.Socks5UdpAssociateAsync(option));
		}
	}
}
