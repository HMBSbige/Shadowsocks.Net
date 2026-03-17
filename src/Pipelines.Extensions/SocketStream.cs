using System.Net.Sockets;

namespace Pipelines.Extensions;

/// <summary>
/// A <see cref="Stream"/> implementation that wraps a <see cref="System.Net.Sockets.Socket"/> for read/write operations.
/// </summary>
public sealed class SocketStream : Stream
{
	private readonly SocketFlags _socketFlags;
	private readonly bool _ownsSocket;
	private bool _disposed;

	/// <summary>
	/// Initializes a new instance of <see cref="SocketStream"/>.
	/// </summary>
	/// <param name="socket">The socket to wrap.</param>
	/// <param name="socketFlags">The socket flags used for send/receive operations.</param>
	/// <param name="ownsSocket">If <see langword="true"/>, the socket is closed when this stream is disposed.</param>
	public SocketStream(Socket socket, SocketFlags socketFlags = SocketFlags.None, bool ownsSocket = false)
	{
		ArgumentNullException.ThrowIfNull(socket);

		Socket = socket;
		_socketFlags = socketFlags;
		_ownsSocket = ownsSocket;
	}

	/// <summary>
	/// Gets the underlying <see cref="System.Net.Sockets.Socket"/>.
	/// </summary>
	public Socket Socket { get; }

	/// <inheritdoc />
	public override bool CanRead => !_disposed;

	/// <inheritdoc />
	public override bool CanWrite => !_disposed;

	/// <inheritdoc />
	public override bool CanSeek => false;

	/// <inheritdoc />
	public override long Length => throw new NotSupportedException();

	/// <inheritdoc />
	public override long Position
	{
		get => throw new NotSupportedException();
		set => throw new NotSupportedException();
	}

	/// <inheritdoc />
	public override long Seek(long offset, SeekOrigin origin)
	{
		throw new NotSupportedException();
	}

	/// <inheritdoc />
	public override void SetLength(long value)
	{
		throw new NotSupportedException();
	}

	/// <inheritdoc />
	public override int Read(byte[] buffer, int offset, int count)
	{
		return Read(buffer.AsSpan(offset, count));
	}

	/// <inheritdoc />
	public override int Read(Span<byte> buffer)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return Socket.Receive(buffer, _socketFlags);
	}

	/// <inheritdoc />
	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
	}

	/// <inheritdoc />
	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return Socket.ReceiveAsync(buffer, _socketFlags, cancellationToken);
	}

	/// <inheritdoc />
	public override void Write(byte[] buffer, int offset, int count)
	{
		Write(buffer.AsSpan(offset, count));
	}

	/// <inheritdoc />
	public override void Write(ReadOnlySpan<byte> buffer)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		Socket.Send(buffer, _socketFlags);
	}

	/// <inheritdoc />
	public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
	}

	/// <inheritdoc />
	public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		ValueTask<int> sendTask = Socket.SendAsync(buffer, _socketFlags, cancellationToken);

		if (sendTask.IsCompletedSuccessfully)
		{
			_ = sendTask.GetAwaiter().GetResult();
			return default;
		}

		return AwaitSend(sendTask);

		static async ValueTask AwaitSend(ValueTask<int> task)
		{
			await task;
		}
	}

	/// <inheritdoc />
	public override void Flush()
	{
		// no-op: sockets are unbuffered
	}

	/// <inheritdoc />
	public override Task FlushAsync(CancellationToken cancellationToken)
	{
		return cancellationToken.IsCancellationRequested
			? Task.FromCanceled(cancellationToken)
			: Task.CompletedTask;
	}

	/// <inheritdoc />
	protected override void Dispose(bool disposing)
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;

		if (disposing && _ownsSocket)
		{
			Socket.FullClose();
		}

		base.Dispose(disposing);
	}
}
