using Microsoft;
using Microsoft.VisualStudio.Threading;
using Shadowsocks.Crypto;
using Shadowsocks.Protocol.Models;
using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.TcpClients
{
	internal class ShadowsocksPipeWriter : PipeWriter
	{
		public PipeWriter InternalWriter { get; }

		private readonly Pipe _pipe;
		private PipeWriter Writer => _pipe.Writer;
		private PipeReader Reader => _pipe.Reader;

		private readonly CancellationTokenSource _cancellationTokenSource;

		private const int BufferSize = ShadowsocksProtocolConstants.SendBufferSize;

		public ShadowsocksPipeWriter(
			PipeWriter writer,
			ShadowsocksServerInfo serverInfo,
			PipeOptions? pipeOptions = null)
		{
			Requires.NotNull(writer, nameof(writer));
			Requires.NotNull(serverInfo, nameof(serverInfo));
			Requires.NotNullAllowStructs(serverInfo.Method, nameof(serverInfo));
			Requires.NotNullAllowStructs(serverInfo.Password, nameof(serverInfo));

			var encryptor = ShadowsocksCrypto.Create(serverInfo.Method, serverInfo.Password);

			InternalWriter = writer;
			_pipe = new Pipe(pipeOptions ?? PipeOptions.Default);
			_cancellationTokenSource = new CancellationTokenSource();

			WrapAsync(encryptor, _cancellationTokenSource.Token).Forget();
		}

		private Task WrapAsync(IShadowsocksCrypto encryptor, CancellationToken cancellationToken)
		{
			return Task.Run(
				async () =>
				{
					try
					{
						while (true)
						{
							var result = await Reader.ReadAsync(cancellationToken);
							var buffer = result.Buffer;

							foreach (var segment in buffer)
							{
								SendToRemote(segment.Span);
								var flushResult = await InternalWriter.FlushAsync(cancellationToken);
								if (flushResult.IsCompleted)
								{
									goto NoData;
								}
							}

							Reader.AdvanceTo(buffer.End);

							if (result.IsCompleted)
							{
								break;
							}
						}
					NoData:
						await Reader.CompleteAsync();
					}
					catch (Exception ex)
					{
						await Reader.CompleteAsync(ex);
					}
					finally
					{
						encryptor.Dispose();
					}
				},
				default
			);

			void SendToRemote(ReadOnlySpan<byte> buffer)
			{
				while (!buffer.IsEmpty)
				{
					var span = InternalWriter.GetSpan(BufferSize);

					encryptor.EncryptTCP(buffer, span, out var p, out var outLength);

					InternalWriter.Advance(outLength);

					if (p == buffer.Length)
					{
						break;
					}

					buffer = buffer[p..];
				}
			}
		}

		public override void Advance(int bytes)
		{
			Writer.Advance(bytes);
		}

		public override Memory<byte> GetMemory(int sizeHint = 0)
		{
			return Writer.GetMemory(sizeHint);
		}

		public override Span<byte> GetSpan(int sizeHint = 0)
		{
			return Writer.GetSpan(sizeHint);
		}

		public override void CancelPendingFlush()
		{
			Writer.CancelPendingFlush();
		}

		public override void Complete(Exception? exception = null)
		{
			_cancellationTokenSource.Cancel();
			Writer.Complete(exception);
		}

		public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
		{
			return Writer.FlushAsync(cancellationToken);
		}
	}
}
