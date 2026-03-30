using Socks5.Protocol;
using System.Buffers;

namespace Socks5;

public static class Socks5Utils
{
	public static bool IsSocks5Header(this ReadOnlySequence<byte> buffer)
	{
		SequenceReader<byte> reader = new(buffer);

		if (!reader.TryRead(out byte ver))
		{
			return false;
		}

		if (ver is not Constants.ProtocolVersion)
		{
			return false;
		}

		if (!reader.TryRead(out byte num) || num is 0)
		{
			return false;
		}

		if (reader.Remaining < num)
		{
			return false;
		}

		return true;
	}
}
