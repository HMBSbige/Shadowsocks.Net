using System.IO.Pipelines;
using System.Net.Sockets;

namespace Pipelines.Extensions.SocketPipe;

internal sealed class SocketPipeReader : PipeReader
{
	public Socket InternalSocket { get; }

	private readonly SocketPipeReaderOptions _options;
	private readonly Pipe _pipe;

	private PipeWriter Writer => _pipe.Writer;

	private PipeReader Reader => _pipe.Reader;

	private readonly CancellationTokenSource _cancellationTokenSource;

	public SocketPipeReader(Socket socket, SocketPipeReaderOptions options)
	{
		ArgumentNullException.ThrowIfNull(socket);

		if (!socket.Connected)
		{
			throw new ArgumentException(@"Socket must be connected.", nameof(socket));
		}

		ArgumentNullException.ThrowIfNull(options);

		InternalSocket = socket;
		_options = options;
		_pipe = new Pipe(options.PipeOptions);
		_cancellationTokenSource = new CancellationTokenSource();

		_ = Task.Run(() => FillPipeAsync(_cancellationTokenSource.Token));
	}

	private async Task FillPipeAsync(CancellationToken cancellationToken)
	{
		try
		{
			while (true)
			{
				Memory<byte> memory = Writer.GetMemory(_options.SizeHint);

				int readLength = await InternalSocket.ReceiveAsync(memory, _options.SocketFlags, cancellationToken);

				if (readLength is 0)
				{
					break;
				}

				Writer.Advance(readLength);

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
		try
		{
			_cancellationTokenSource.Cancel();
			Reader.Complete(exception);
		}
		finally
		{
			_cancellationTokenSource.Dispose();
			CloseSocketIfNeeded();
		}

		return;

		void CloseSocketIfNeeded()
		{
			try
			{
				if (_options.ShutDownReceive)
				{
					InternalSocket.Shutdown(SocketShutdown.Receive);
				}
			}
			finally
			{
				if (!_options.LeaveOpen)
				{
					InternalSocket.FullClose();
				}
			}
		}
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
