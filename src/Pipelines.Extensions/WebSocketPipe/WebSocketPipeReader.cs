using Microsoft;
using Microsoft.VisualStudio.Threading;
using System.IO.Pipelines;
using System.Net.WebSockets;

namespace Pipelines.Extensions.WebSocketPipe;

internal sealed class WebSocketPipeReader : PipeReader
{
	public WebSocket InternalWebSocket { get; }

	private readonly WebSocketPipeReaderOptions _options;
	private readonly Pipe _pipe;
	private PipeWriter Writer => _pipe.Writer;
	private PipeReader Reader => _pipe.Reader;

	private readonly CancellationTokenSource _cancellationTokenSource;

	public WebSocketPipeReader(WebSocket webSocket, WebSocketPipeReaderOptions options)
	{
		Requires.NotNull(webSocket, nameof(webSocket));
		Requires.NotNull(options, nameof(options));

		InternalWebSocket = webSocket;
		_options = options;
		_pipe = new Pipe(options.PipeOptions);
		_cancellationTokenSource = new CancellationTokenSource();

		WrapWriterAsync(_cancellationTokenSource.Token).Forget();
	}

	private Task WrapWriterAsync(CancellationToken cancellationToken)
	{
		return Task.Run(
			async () =>
			{
				try
				{
					while (true)
					{
						Memory<byte> memory = Writer.GetMemory(_options.SizeHint);

						ValueWebSocketReceiveResult readResult = await InternalWebSocket.ReceiveAsync(memory, cancellationToken);

						if (readResult.Count is 0)
						{
							break;
						}

						Writer.Advance(readResult.Count);

						FlushResult flushResult = await Writer.FlushAsync(cancellationToken);
						if (flushResult.IsCompleted)
						{
							break;
						}
					}

					await Writer.CompleteAsync();
				}
				catch (Exception ex)
				{
					await Writer.CompleteAsync(ex);
				}
			},
			cancellationToken
		);
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
