using Microsoft.VisualStudio.TestTools.UnitTesting;
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
			var ipe = new IPEndPoint(IPAddress.Loopback, 23333);
			Assert.IsTrue(await TestUtils.Socks5ConnectAsync(ipe));
		}

		[TestMethod]
		public async Task UdpAssociateTestAsync()
		{
			var ipe = new IPEndPoint(IPAddress.Loopback, 23333);
			Assert.IsTrue(await TestUtils.Socks5UdpAssociateAsync(ipe));
		}
	}
}
