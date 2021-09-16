using Microsoft;
using Socks5.Enums;
using Socks5.Utils;
using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Socks5.Servers
{
	/// <summary>
	/// Just for test use only
	/// </summary>
	public class SimpleSocks5UdpServer : IDisposable
	{
		public UdpClient UdpListener { get; }
		private readonly IPEndPoint _remoteEndpoint;

		private readonly CancellationTokenSource _cts;

		private const int MaxUdpSize = 0x10000;

		public SimpleSocks5UdpServer(IPEndPoint bindEndPoint, IPEndPoint remoteEndpoint)
		{
			_remoteEndpoint = remoteEndpoint;
			Requires.NotNull(bindEndPoint, nameof(bindEndPoint));
			Requires.NotNull(remoteEndpoint, nameof(remoteEndpoint));

			UdpListener = new UdpClient(bindEndPoint);

			_cts = new CancellationTokenSource();
		}

		public async ValueTask StartAsync()
		{
			try
			{
				while (!_cts.IsCancellationRequested)
				{
					//TODO .NET6.0
					var message = await UdpListener.ReceiveAsync();
					if (Equals(message.RemoteEndPoint.Address, _remoteEndpoint.Address))
					{
						await HandleAsync(message, _cts.Token);
					}
				}
			}
			catch (Exception)
			{
				Dispose();
			}
		}

		private async ValueTask HandleAsync(UdpReceiveResult result, CancellationToken token)
		{
			if (token.IsCancellationRequested)
			{
				return;
			}

			var socks5UdpPacket = Unpack.Udp(result.Buffer);
			if (socks5UdpPacket.Fragment is not 0x00)
			{
				return; // Ignore
			}

			UdpClient client;
			if (socks5UdpPacket.Type is AddressType.Domain)
			{
				Assumes.NotNull(socks5UdpPacket.Domain);
				client = new UdpClient(socks5UdpPacket.Domain, socks5UdpPacket.Port);
			}
			else
			{
				Assumes.NotNull(socks5UdpPacket.Address);
				client = new UdpClient(socks5UdpPacket.Address.AddressFamily);
				client.Connect(socks5UdpPacket.Address, socks5UdpPacket.Port);
			}

			await client.Client.SendAsync(socks5UdpPacket.Data, SocketFlags.None, token);

			var headerLength = result.Buffer.Length - socks5UdpPacket.Data.Length;
			var receiveBuffer = ArrayPool<byte>.Shared.Rent(MaxUdpSize);
			try
			{
				var receiveLength = await client.Client.ReceiveAsync(receiveBuffer.AsMemory(headerLength), SocketFlags.None, token);
				result.Buffer.AsSpan(0, headerLength).CopyTo(receiveBuffer);

				//TODO .NET6.0
				await UdpListener.Client.SendToAsync(
					new ArraySegment<byte>(receiveBuffer, 0, headerLength + receiveLength),
					SocketFlags.None,
					result.RemoteEndPoint
				);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(receiveBuffer);
			}
		}

		public void Dispose()
		{
			try
			{
				UdpListener.Dispose();
			}
			finally
			{
				_cts.Cancel();
			}
		}
	}
}
