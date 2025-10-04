using System.Buffers;
using System.IO.Pipelines;

namespace Shadowsocks.Protocol.LocalTcpServices;

public interface ILocalTcpService
{
	bool IsHandle(ReadOnlySequence<byte> buffer);

	ValueTask HandleAsync(IDuplexPipe pipe, CancellationToken token = default);
}
