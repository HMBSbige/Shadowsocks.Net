using Microsoft;
using Socks5.Enums;
using Socks5.Models;
using Socks5.Utils;
using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace Socks5.Servers;

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
			while (true)
			{
				UdpReceiveResult message = await UdpListener.ReceiveAsync(_cts.Token);
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

		Socks5UdpReceivePacket socks5UdpPacket = Unpack.Udp(result.Buffer);
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

		int headerLength = result.Buffer.Length - socks5UdpPacket.Data.Length;
		byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(MaxUdpSize);
		try
		{
			int receiveLength = await client.Client.ReceiveAsync(receiveBuffer.AsMemory(headerLength), SocketFlags.None, token);
			result.Buffer.AsSpan(0, headerLength).CopyTo(receiveBuffer);

			await UdpListener.Client.SendToAsync(
				receiveBuffer.AsMemory(0, headerLength + receiveLength),
				SocketFlags.None,
				result.RemoteEndPoint,
				token
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
