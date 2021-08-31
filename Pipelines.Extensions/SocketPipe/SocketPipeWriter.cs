using Microsoft;
using Microsoft.VisualStudio.Threading;
using System;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Pipelines.Extensions.SocketPipe
{
	internal sealed class SocketPipeWriter : PipeWriter
	{
		public Socket InternalSocket { get; }

		private readonly SocketPipeWriterOptions _options;
		private readonly Pipe _pipe;
		private PipeWriter Writer => _pipe.Writer;
		private PipeReader Reader => _pipe.Reader;

		private readonly CancellationTokenSource _cancellationTokenSource;

		public SocketPipeWriter(Socket socket, SocketPipeWriterOptions options)
		{
			Requires.NotNull(socket, nameof(socket));
			Requires.Argument(socket.Connected, nameof(socket), @"Socket must be connected.");
			Requires.NotNull(options, nameof(options));

			InternalSocket = socket;
			_options = options;
			_pipe = new Pipe(options.PipeOptions);
			_cancellationTokenSource = new CancellationTokenSource();

			WrapReaderAsync(_cancellationTokenSource.Token).Forget();
		}

		private Task WrapReaderAsync(CancellationToken cancellationToken)
		{
			return Task.Run(async () =>
			{
				try
				{
					while (true)
					{
						var result = await Reader.ReadAndCheckIsCanceledAsync(cancellationToken);
						var buffer = result.Buffer;

						foreach (var memory in buffer)
						{
							var length = await InternalSocket.SendAsync(memory, _options.SocketFlags, cancellationToken);
							Report.IfNot(length == memory.Length);
						}

						Reader.AdvanceTo(buffer.End);

						if (result.IsCompleted)
						{
							break;
						}
					}

					await Reader.CompleteAsync();
				}
				catch (Exception ex)
				{
					await Reader.CompleteAsync(ex);
				}
			}, cancellationToken);
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
