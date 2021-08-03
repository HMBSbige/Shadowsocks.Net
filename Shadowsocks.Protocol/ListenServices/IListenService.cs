using System.Threading.Tasks;

namespace Shadowsocks.Protocol.ListenServices
{
	public interface IListenService
	{
		ValueTask StartAsync();
		void Stop();
	}
}
