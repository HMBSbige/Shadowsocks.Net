using Microsoft;
using Microsoft.VisualStudio.Threading;
using Pipelines.Extensions;
using Socks5.Models;
using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace HttpProxy
{
	public class HttpSocks5Service
	{
		public TcpListener TcpListener { get; }

		private readonly HttpToSocks5 _httpToSocks5;
		private readonly Socks5CreateOption _socks5CreateOption;
		private readonly CancellationTokenSource _cts;

		public HttpSocks5Service(IPEndPoint bindEndPoint, HttpToSocks5 httpToSocks5, Socks5CreateOption socks5CreateOption)
		{
			Requires.NotNullAllowStructs(socks5CreateOption.Address, nameof(socks5CreateOption.Address));

			TcpListener = new TcpListener(bindEndPoint);
			_httpToSocks5 = httpToSocks5;
			_socks5CreateOption = socks5CreateOption;

			_cts = new CancellationTokenSource();
		}

		public async ValueTask StartAsync()
		{
			try
			{
				TcpListener.Start();
				while (!_cts.IsCancellationRequested)
				{
					var rec = await TcpListener.AcceptTcpClientAsync();
					rec.NoDelay = true;
					HandleAsync(rec, _cts.Token).Forget();
				}
			}
			catch (Exception)
			{
				Stop();
			}
		}

		private async ValueTask HandleAsync(TcpClient rec, CancellationToken token)
		{
			try
			{
				var pipe = rec.GetStream().AsDuplexPipe();
				var result = await pipe.Input.ReadAsync(token);
				var buffer = result.Buffer;

				if (IsSocks5Header(buffer))
				{
					using var socks5 = new TcpClient();
					await socks5.ConnectAsync(_socks5CreateOption.Address!, _socks5CreateOption.Port, token);
					var socks5Pipe = socks5.GetStream().AsDuplexPipe();

					await socks5Pipe.Output.WriteAsync(buffer, token);
					pipe.Input.AdvanceTo(buffer.End);

					await socks5Pipe.LinkToAsync(pipe, token);
				}
				else
				{
					pipe.Input.AdvanceTo(buffer.Start, buffer.End);
					pipe.Input.CancelPendingRead();

					await _httpToSocks5.ForwardToSocks5Async(pipe, _socks5CreateOption, token);
				}
			}
			finally
			{
				rec.Dispose();
			}

			static bool IsSocks5Header(ReadOnlySequence<byte> buffer)
			{
				var reader = new SequenceReader<byte>(buffer);
				return reader.TryRead(out var ver) && ver is 0x05;
			}
		}

		public void Stop()
		{
			try
			{
				TcpListener.Stop();
			}
			finally
			{
				_cts.Cancel();
			}
		}
	}
}
