using Microsoft;
using Microsoft.VisualStudio.Threading;
using Shadowsocks.Crypto;
using Shadowsocks.Protocol.Models;
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Protocol.TcpClients
{
	public class ShadowsocksDuplexPipe : IDuplexPipe
	{
		public PipeReader Input { get; }
		public PipeWriter Output { get; }

		private readonly IDuplexPipe _upPipe;
		private const int BufferSize = 20 * 4096;

		public ShadowsocksDuplexPipe(
			IDuplexPipe pipe,
			ShadowsocksServerInfo serverInfo,
			PipeOptions? pipeOptions = null,
			CancellationToken cancellationToken = default)
		{
			Requires.NotNull(pipe, nameof(pipe));
			Requires.NotNull(serverInfo, nameof(serverInfo));

			var encryptor = ShadowsocksCrypto.Create(serverInfo.Method!, serverInfo.Password!);
			var decryptor = ShadowsocksCrypto.Create(serverInfo.Method!, serverInfo.Password!);

			pipeOptions ??= PipeOptions.Default;

			_upPipe = pipe;

			Input = WrapReader(decryptor, pipeOptions, cancellationToken);
			Output = WrapWriter(encryptor, pipeOptions, cancellationToken);
		}

		private PipeWriter WrapWriter(
			IShadowsocksCrypto encryptor,
			PipeOptions pipeOptions,
			CancellationToken cancellationToken = default)
		{
			var pipe = new Pipe(pipeOptions);
			Task.Run(async () =>
			{
				try
				{
					while (true)
					{
						var result = await pipe.Reader.ReadAsync(cancellationToken);
						var buffer = result.Buffer;

						if (!buffer.IsEmpty)
						{
							foreach (var segment in buffer)
							{
								SendToRemote(encryptor, _upPipe.Output, segment);
							}

							var flushResult = await _upPipe.Output.FlushAsync(cancellationToken);
							if (flushResult.IsCompleted)
							{
								break;
							}
						}

						pipe.Reader.AdvanceTo(buffer.End);

						if (result.IsCompleted)
						{
							break;
						}
					}

					await pipe.Reader.CompleteAsync();
				}
				catch (Exception ex)
				{
					await pipe.Reader.CompleteAsync(ex);
				}
				finally
				{
					encryptor.Dispose();
				}
			}, cancellationToken).Forget();
			return pipe.Writer;
		}

		private PipeReader WrapReader(
			IShadowsocksCrypto decryptor,
			PipeOptions pipeOptions,
			CancellationToken cancellationToken = default)
		{
			var pipe = new Pipe(pipeOptions);
			Task.Run(async () =>
			{
				try
				{
					while (true)
					{
						var result = await _upPipe.Input.ReadAsync(cancellationToken);
						if (result.IsCanceled)
						{
							cancellationToken.ThrowIfCancellationRequested();
							throw new OperationCanceledException();
						}

						var buffer = result.Buffer;
						try
						{
							if (ReceiveFromRemote(decryptor, pipe.Writer, ref buffer))
							{
								var writerFlushResult = await pipe.Writer.FlushAsync(cancellationToken);
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
							_upPipe.Input.AdvanceTo(buffer.Start, buffer.End);
						}
					}

					await pipe.Writer.CompleteAsync();
				}
				catch (Exception ex)
				{
					await pipe.Writer.CompleteAsync(ex);
				}
				finally
				{
					decryptor.Dispose();
				}
			}, cancellationToken).Forget();
			return pipe.Reader;
		}

		private void SendToRemote(
			IShadowsocksCrypto encryptor,
			PipeWriter writer,
			ReadOnlyMemory<byte> buffer)
		{
			while (!buffer.IsEmpty)
			{
				var memory = writer.GetMemory(BufferSize);

				encryptor.EncryptTCP(buffer.Span, memory.Span, out var p, out var outLength);

				writer.Advance(outLength);

				if (p == buffer.Length)
				{
					break;
				}

				buffer = buffer[p..];
			}
		}

		private bool ReceiveFromRemote(
			IShadowsocksCrypto decryptor,
			PipeWriter writer,
			ref ReadOnlySequence<byte> sequence)
		{
			var result = false;
			while (!sequence.IsEmpty)
			{
				var oldLength = sequence.Length;

				var memory = writer.GetMemory(BufferSize);

				var outLength = decryptor.DecryptTCP(ref sequence, memory.Span);

				writer.Advance(outLength);
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
}
