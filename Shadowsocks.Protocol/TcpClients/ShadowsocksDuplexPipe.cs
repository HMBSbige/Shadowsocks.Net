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
	public class ShadowsocksDuplexPipe : IDuplexPipe
	{
		public PipeReader Input { get; }
		public PipeWriter Output { get; }

		private readonly IDuplexPipe _upPipe;
		private const int BufferSize = 20 * 4096;

		public ShadowsocksDuplexPipe(
			IDuplexPipe pipe,
			ShadowsocksServerInfo serverInfo,
			string targetAddress, ushort targetPort,
			PipeOptions? pipeOptions = null,
			CancellationToken cancellationToken = default)
		{
			Requires.NotNull(pipe, nameof(pipe));
			Requires.NotNull(serverInfo, nameof(serverInfo));
			Requires.NotNullAllowStructs(serverInfo.Method, nameof(serverInfo));
			Requires.NotNullAllowStructs(serverInfo.Password, nameof(serverInfo));

			var encryptor = ShadowsocksCrypto.Create(serverInfo.Method, serverInfo.Password);
			var decryptor = ShadowsocksCrypto.Create(serverInfo.Method, serverInfo.Password);

			pipeOptions ??= PipeOptions.Default;

			_upPipe = pipe;

			Input = WrapReader(decryptor, pipeOptions, cancellationToken);
			Output = WrapWriter(encryptor, pipeOptions, cancellationToken);
			Output.WriteShadowsocksHeader(targetAddress, targetPort);
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
								SendToRemote(encryptor, _upPipe.Output, segment.Span);
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
						var result = await _upPipe.Input.ReadAndCheckIsCanceledAsync(cancellationToken);

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

		private static void SendToRemote(
			IShadowsocksCrypto encryptor,
			PipeWriter writer,
			ReadOnlySpan<byte> buffer)
		{
			while (!buffer.IsEmpty)
			{
				var span = writer.GetSpan(BufferSize);

				encryptor.EncryptTCP(buffer, span, out var p, out var outLength);

				writer.Advance(outLength);

				if (p == buffer.Length)
				{
					break;
				}

				buffer = buffer[p..];
			}
		}

		private static bool ReceiveFromRemote(
			IShadowsocksCrypto decryptor,
			PipeWriter writer,
			ref ReadOnlySequence<byte> sequence)
		{
			var result = false;
			while (!sequence.IsEmpty)
			{
				var oldLength = sequence.Length;

				var span = writer.GetSpan(BufferSize);

				var outLength = decryptor.DecryptTCP(ref sequence, span);

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
