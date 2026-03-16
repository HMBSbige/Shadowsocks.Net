using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;

namespace Pipelines.Extensions;

internal class DuplexPipeStream : Stream
{
	private readonly Stream _readStream;
	private readonly Stream _writeStream;

	public DuplexPipeStream(IDuplexPipe pipe, bool leaveOpen)
	{
		ArgumentNullException.ThrowIfNull(pipe);

		_readStream = pipe.Input.AsStream(leaveOpen);
		_writeStream = pipe.Output.AsStream(leaveOpen);
	}

	public override bool CanRead => !IsDisposed;

	public override bool CanSeek => false;

	public override bool CanWrite => !IsDisposed;

	public override long Length => throw ThrowNotSupported();

	public override long Position
	{
		get => throw ThrowNotSupported();
		set => throw ThrowNotSupported();
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		throw ThrowNotSupported();
	}

	public override void SetLength(long value)
	{
		ThrowNotSupported();
	}

	public override int ReadByte()
	{
		return _readStream.ReadByte();
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		return _readStream.Read(buffer.AsSpan(offset, count));
	}

	public override int Read(Span<byte> buffer)
	{
		return _readStream.Read(buffer);
	}

	public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		return await _readStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
	}

	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		return _readStream.ReadAsync(buffer, cancellationToken);
	}

	public override void WriteByte(byte value)
	{
		_writeStream.WriteByte(value);
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		_writeStream.Write(buffer.AsSpan(offset, count));
	}

	public override void Write(ReadOnlySpan<byte> buffer)
	{
		_writeStream.Write(buffer);
	}

	public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		await _writeStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
	}

	public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
	{
		return _writeStream.WriteAsync(buffer, cancellationToken);
	}

	public override void Flush()
	{
		_writeStream.Flush();
	}

	public override Task FlushAsync(CancellationToken cancellationToken)
	{
		return _writeStream.FlushAsync(cancellationToken);
	}

	public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
	{
		return _readStream.CopyToAsync(destination, bufferSize, cancellationToken);
	}

	public bool IsDisposed { get; private set; }
	protected override void Dispose(bool disposing)
	{
		if (IsDisposed)
		{
			return;
		}

		IsDisposed = true;

		_readStream.Dispose();
		_writeStream.Dispose();

		base.Dispose(disposing);
	}

	[DoesNotReturn]
	private Exception ThrowNotSupported()
	{
		ObjectDisposedException.ThrowIf(IsDisposed, this);
		throw new NotSupportedException();
	}
}
