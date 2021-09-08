using Microsoft;
using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Pipelines.Extensions
{
	internal class ReadOnlySequenceStream : Stream, IDisposableObservable
	{
		public override bool CanRead => !IsDisposed;

		public override bool CanSeek => !IsDisposed;

		public override bool CanWrite => false;

		public override long Length
		{
			get
			{
				Verify.NotDisposed(this);
				return _readOnlySequence.Length;
			}
		}

		public override long Position
		{
			get
			{
				Verify.NotDisposed(this);
				return _readOnlySequence.Slice(0, _position).Length;
			}
			set
			{
				Verify.NotDisposed(this);
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
			Verify.NotDisposed(this);

			var remaining = _readOnlySequence.Slice(_position);
			var sequence = remaining.Slice(0, Math.Min(buffer.Length, remaining.Length));
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
			Verify.NotDisposed(this);

			var pos = origin switch
			{
				SeekOrigin.Begin   => default,
				SeekOrigin.Current => Position,
				SeekOrigin.End     => _readOnlySequence.Length,
				_                  => throw Requires.FailRange(nameof(origin))
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
			Verify.NotDisposed(this);
			throw new NotSupportedException();
		}

		#endregion
	}
}
