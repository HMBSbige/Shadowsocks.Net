using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace Pipelines.Extensions.SocketPipe;

internal sealed class SocketPipeWriter : PipeWriter
{
	public Socket InternalSocket { get; }

	private readonly SocketPipeWriterOptions _options;
	private readonly ArrayBufferWriter<byte> _buffer = new();
	private int _bytesSent;
	private volatile bool _canceled;
	private bool _completed;
	private CancellationTokenSource _flushCts = new();

	public override bool CanGetUnflushedBytes => true;

	public override long UnflushedBytes => _buffer.WrittenCount - _bytesSent;

	public SocketPipeWriter(Socket socket, SocketPipeWriterOptions options)
	{
		ArgumentNullException.ThrowIfNull(socket);

		if (!socket.Connected)
		{
			throw new ArgumentException(@"Socket must be connected.", nameof(socket));
		}

		ArgumentNullException.ThrowIfNull(options);

		InternalSocket = socket;
		_options = options;
	}

	public override void Advance(int bytes)
	{
		ThrowIfCompleted();
		_buffer.Advance(bytes);
	}

	public override Memory<byte> GetMemory(int sizeHint = 0)
	{
		ThrowIfCompleted();
		return _buffer.GetMemory(sizeHint);
	}

	public override Span<byte> GetSpan(int sizeHint = 0)
	{
		ThrowIfCompleted();
		return _buffer.GetSpan(sizeHint);
	}

	public override void CancelPendingFlush()
	{
		if (_completed)
		{
			return;
		}

		_canceled = true;
		_flushCts.Cancel();
	}

	public override void Complete(Exception? exception = null)
	{
		_completed = true;

		try
		{
			_flushCts.Dispose();
			_buffer.ResetWrittenCount();
			_bytesSent = 0;
		}
		finally
		{
			CloseSocketIfNeeded();
		}

		return;

		void CloseSocketIfNeeded()
		{
			try
			{
				if (_options.ShutDownSend)
				{
					InternalSocket.Shutdown(SocketShutdown.Send);
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

	private void ThrowIfCompleted()
	{
		if (_completed)
		{
			throw new InvalidOperationException("Writing is not allowed after writer was completed.");
		}
	}

	public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfCompleted();

		if (cancellationToken.IsCancellationRequested)
		{
			return new ValueTask<FlushResult>(Task.FromCanceled<FlushResult>(cancellationToken));
		}

		if (_canceled)
		{
			_canceled = false;
			return new ValueTask<FlushResult>(new FlushResult(true, _completed));
		}

		if (_buffer.WrittenCount <= _bytesSent)
		{
			return new ValueTask<FlushResult>(new FlushResult(false, _completed));
		}

		return SendAsync(cancellationToken);
	}

	private async ValueTask<FlushResult> SendAsync(CancellationToken cancellationToken)
	{
		if (!_flushCts.TryReset())
		{
			_flushCts.Dispose();
			_flushCts = new CancellationTokenSource();
		}

		using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _flushCts.Token);

		try
		{
			ReadOnlyMemory<byte> remaining = _buffer.WrittenMemory.Slice(_bytesSent);

			while (remaining.Length > 0)
			{
				int sent = await InternalSocket.SendAsync(remaining, _options.SocketFlags, linkedCts.Token);
				_bytesSent += sent;
				remaining = remaining.Slice(sent);
			}

			_buffer.ResetWrittenCount();
			_bytesSent = 0;
		}
		catch (OperationCanceledException) when (_canceled && !cancellationToken.IsCancellationRequested)
		{
			_canceled = false;
			return new FlushResult(true, _completed);
		}

		return new FlushResult(false, _completed);
	}
}
