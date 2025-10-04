using Microsoft;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;

namespace Pipelines.Extensions.WebSocketPipe;

internal sealed class WebSocketPipeWriter : PipeWriter
{
	public WebSocket InternalWebSocket { get; }

	private readonly Pipe _pipe;
	private PipeWriter Writer => _pipe.Writer;
	private PipeReader Reader => _pipe.Reader;

	public WebSocketPipeWriter(WebSocket webSocket, WebSocketPipeWriterOptions options)
	{
		Requires.NotNull(webSocket, nameof(webSocket));
		Requires.NotNull(options, nameof(options));

		InternalWebSocket = webSocket;
		_pipe = new Pipe(options.PipeOptions);
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
		Writer.Complete(exception);
	}

	public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
	{
		if (Writer.UnflushedBytes <= 0)
		{
			return await Writer.FlushAsync(cancellationToken);
		}

		ValueTask<FlushResult> flushTask = Writer.FlushAsync(cancellationToken);

		try
		{
			ReadResult result = await Reader.ReadAsync(cancellationToken);
			ReadOnlySequence<byte> buffer = result.Buffer;

			foreach (ReadOnlyMemory<byte> memory in buffer)
			{
				await InternalWebSocket.SendAsync(memory, WebSocketMessageType.Binary, true, cancellationToken);
			}

			Reader.AdvanceTo(buffer.End);

			if (result.IsCompleted)
			{
				await Reader.CompleteAsync();
			}
		}
		catch (Exception ex)
		{
			await Reader.CompleteAsync(ex);
			throw;
		}

		return await flushTask;
	}
}
