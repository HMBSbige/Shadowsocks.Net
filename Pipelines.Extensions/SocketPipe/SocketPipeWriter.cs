using Microsoft;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace Pipelines.Extensions.SocketPipe;

internal sealed class SocketPipeWriter : PipeWriter
{
	public Socket InternalSocket { get; }

	private readonly SocketPipeWriterOptions _options;
	private readonly Pipe _pipe;
	private PipeWriter Writer => _pipe.Writer;
	private PipeReader Reader => _pipe.Reader;

	public SocketPipeWriter(Socket socket, SocketPipeWriterOptions options)
	{
		Requires.NotNull(socket, nameof(socket));
		Requires.Argument(socket.Connected, nameof(socket), @"Socket must be connected.");
		Requires.NotNull(options, nameof(options));

		InternalSocket = socket;
		_options = options;
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
		try
		{
			Writer.Complete(exception);
		}
		finally
		{
			CloseSocketIfNeeded();
		}

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
				int length = await InternalSocket.SendAsync(memory, _options.SocketFlags, cancellationToken);
				Report.IfNot(length == memory.Length);
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
