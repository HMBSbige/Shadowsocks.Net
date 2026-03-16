using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Pipelines.Extensions;

internal class ReadOnlySequenceStream : Stream
{
	public override bool CanRead => !IsDisposed;

	public override bool CanSeek => !IsDisposed;

	public override bool CanWrite => false;

	public override long Length
	{
		get
		{
			ObjectDisposedException.ThrowIf(IsDisposed, this);
			return _readOnlySequence.Length;
		}
	}

	public override long Position
	{
		get
		{
			ObjectDisposedException.ThrowIf(IsDisposed, this);
			return _readOnlySequence.Slice(0, _position).Length;
		}
		set
		{
			ObjectDisposedException.ThrowIf(IsDisposed, this);
			_position = _readOnlySequence.GetPosition(value);
		}
	}

	private readonly ReadOnlySequence<byte> _readOnlySequence;
	private SequencePosition _position;

	internal ReadOnlySequenceStream(ReadOnlySequence<byte> readOnlySequence)
	{
		_readOnlySequence = readOnlySequence;
		_position = readOnlySequence.Start;
	}

	public override int Read(Span<byte> buffer)
	{
		ObjectDisposedException.ThrowIf(IsDisposed, this);

		ReadOnlySequence<byte> remaining = _readOnlySequence.Slice(_position);
		ReadOnlySequence<byte> sequence = remaining.Slice(0, Math.Min(buffer.Length, remaining.Length));
		sequence.CopyTo(buffer);
		_position = sequence.End;
		return (int)sequence.Length;
	}

	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		return ValueTask.FromResult(Read(buffer.Span));
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		return Read(buffer.AsSpan(offset, count));
	}

	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		return Task.FromResult(Read(buffer.AsSpan(offset, count)));
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		ObjectDisposedException.ThrowIf(IsDisposed, this);

		long pos = origin switch
		{
			SeekOrigin.Begin => default,
			SeekOrigin.Current => Position,
			SeekOrigin.End => _readOnlySequence.Length,
			_ => throw new ArgumentOutOfRangeException(nameof(origin))
		};

		_position = _readOnlySequence.GetPosition(offset + pos);

		return Position;
	}

	public override void SetLength(long value)
	{
		ThrowNotSupported();
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		ThrowNotSupported();
	}

	public override void Flush()
	{
		ThrowNotSupported();
	}

	#region Dispose

	public bool IsDisposed { get; private set; }
	protected override void Dispose(bool disposing)
	{
		if (IsDisposed)
		{
			return;
		}

		IsDisposed = true;
		base.Dispose(disposing);
	}

	[DoesNotReturn]
	private void ThrowNotSupported()
	{
		ObjectDisposedException.ThrowIf(IsDisposed, this);
		throw new NotSupportedException();
	}

	#endregion
}
