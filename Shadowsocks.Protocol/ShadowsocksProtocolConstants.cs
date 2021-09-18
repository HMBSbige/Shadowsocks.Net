using Pipelines.Extensions.SocketPipe;
using System.IO.Pipelines;

namespace Shadowsocks.Protocol
{
	internal static class ShadowsocksProtocolConstants
	{
		private const int BlockSize = 4096; // 4K
		public const int ReceiveBufferSize = 20 * BlockSize; // 80K, must < 85000 LOH
		public const int SendBufferSize = 5 * BlockSize; // 20K, must < 85000 LOH

		private const int SegmentPoolSize = 16;
		private const int MinimumSegmentSize = BlockSize;
		private const int ResumeWriterThreshold = PauseWriterThreshold / 2;
		private const int PauseWriterThreshold = MinimumSegmentSize * SegmentPoolSize;
		public static readonly PipeOptions DefaultPipeOptions = new(
			pauseWriterThreshold: PauseWriterThreshold,
			resumeWriterThreshold: ResumeWriterThreshold,
			minimumSegmentSize: MinimumSegmentSize);

		public static readonly SocketPipeReaderOptions SocketPipeReaderOptions = new(DefaultPipeOptions, sizeHint: ReceiveBufferSize);
		public static readonly SocketPipeWriterOptions SocketPipeWriterOptions = new(DefaultPipeOptions);
	}
}
