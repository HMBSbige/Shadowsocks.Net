using Microsoft;
using Microsoft.VisualStudio.Threading;
using Pipelines.Extensions;
using Shadowsocks.Crypto;
using Shadowsocks.Protocol.Models;
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.TcpClients
{
	internal class ShadowsocksPipeReader : PipeReader
	{
		public PipeReader InternalReader { get; }

		private readonly Pipe _pipe;
		private PipeWriter Writer => _pipe.Writer;
		private PipeReader Reader => _pipe.Reader;

		private readonly CancellationTokenSource _cancellationTokenSource;

		private const int BufferSize = ShadowsocksProtocolConstants.ReceiveBufferSize;

		public ShadowsocksPipeReader(
			PipeReader reader,
			ShadowsocksServerInfo serverInfo,
			PipeOptions? pipeOptions = null)
		{
			Requires.NotNull(reader, nameof(reader));
			Requires.NotNull(serverInfo, nameof(serverInfo));
			Requires.NotNullAllowStructs(serverInfo.Method, nameof(serverInfo));
			Requires.NotNullAllowStructs(serverInfo.Password, nameof(serverInfo));

			var decryptor = ShadowsocksCrypto.Create(serverInfo.Method, serverInfo.Password);

			InternalReader = reader;
			_pipe = new Pipe(pipeOptions ?? PipeOptions.Default);
			_cancellationTokenSource = new CancellationTokenSource();

			WrapAsync(decryptor, _cancellationTokenSource.Token).Forget();
		}

		private Task WrapAsync(IShadowsocksCrypto decryptor, CancellationToken cancellationToken)
		{
			return Task.Run(
				async () =>
				{
					try
					{
						while (true)
						{
							var result = await InternalReader.ReadAndCheckIsCanceledAsync(cancellationToken);
							var buffer = result.Buffer;

							try
							{
								if (ReceiveFromRemote(ref buffer))
								{
									var writerFlushResult = await Writer.FlushAsync(cancellationToken);
									if (writerFlushResult.IsCompleted)
									{
										break;
									}
								}

								if (result.IsCompleted)
								{
									break;
								}
							}
							finally
							{
								InternalReader.AdvanceTo(buffer.Start, buffer.End);
							}
						}

						await Writer.CompleteAsync();
					}
					catch (Exception ex)
					{
						await Writer.CompleteAsync(ex);
					}
					finally
					{
						decryptor.Dispose();
					}
				},
				default
			);

			bool ReceiveFromRemote(ref ReadOnlySequence<byte> sequence)
			{
				var result = false;
				while (!sequence.IsEmpty)
				{
					var oldLength = sequence.Length;

					var span = Writer.GetSpan(BufferSize);

					var outLength = decryptor.DecryptTCP(ref sequence, span);

					Writer.Advance(outLength);
					if (outLength > 0)
					{
						result = true;
					}

					if (oldLength == sequence.Length)
					{
						break;
					}
				}

				return result;
			}
		}

		public override void AdvanceTo(SequencePosition consumed)
		{
			Reader.AdvanceTo(consumed);
		}

		public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
		{
			Reader.AdvanceTo(consumed, examined);
		}

		public override void CancelPendingRead()
		{
			Reader.CancelPendingRead();
		}

		public override void Complete(Exception? exception = null)
		{
			_cancellationTokenSource.Cancel();
			Reader.Complete(exception);
		}

		public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
		{
			return Reader.ReadAsync(cancellationToken);
		}

		public override bool TryRead(out ReadResult result)
		{
			return Reader.TryRead(out result);
		}
	}
}
