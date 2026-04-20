using Proxy.Abstractions;
using Socks5;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Text;

namespace UnitTest.TestBase;

public static class Socks5TestUtils
{
	public static Socks5Inbound CreateInbound(UserPassAuth? userPassAuth = null, IPAddress? udpRelayBindAddress = null)
	{
		return new(new Socks5InboundOption
		{
			UserPassAuth = userPassAuth,
			UdpRelayBindAddress = udpRelayBindAddress
		});
	}

	public static Socks5Outbound CreateOutbound(ushort port, IPAddress? address = null, UserPassAuth? userPassAuth = null)
	{
		return new(CreateOutboundOption(port, address, userPassAuth));
	}

	public static Socks5OutboundOption CreateOutboundOption(ushort port, IPAddress? address = null, UserPassAuth? userPassAuth = null)
	{
		return new()
		{
			Address = address ?? IPAddress.Loopback,
			Port = port,
			UserPassAuth = userPassAuth
		};
	}

	private static ReadOnlySpan<byte> Newline => "\r\n"u8;

	/// <summary>
	/// 使用 HTTP1.1 2xx 测试 SOCKS5 CONNECT (via IStreamOutbound)
	/// </summary>
	public static async ValueTask<bool> Socks5ConnectAsync(
		Socks5OutboundOption option,
		string target,
		string targetHost,
		ushort targetPort,
		CancellationToken cancellationToken = default)
	{
		string sendString = $"GET {target} HTTP/1.1\r\nHost: {targetHost}\r\n\r\n";

		Socks5Outbound outbound = new(option);
		ProxyDestination destination = new(Encoding.ASCII.GetBytes(targetHost), targetPort);

		await using IConnection connection = await outbound.ConnectAsync(destination, cancellationToken);

		Encoding.UTF8.GetBytes(sendString, connection.Output);
		await connection.Output.FlushAsync(cancellationToken);

		string? content;

		while (true)
		{
			ReadResult result = await connection.Input.ReadAsync(cancellationToken);
			ReadOnlySequence<byte> buffer = result.Buffer;

			try
			{
				if (TryReadLine(ref buffer, out content))
				{
					break;
				}

				if (result.IsCompleted)
				{
					break;
				}
			}
			finally
			{
				connection.Input.AdvanceTo(buffer.Start, buffer.End);
			}
		}

		return content is not null && content.StartsWith("HTTP/1.1 2", StringComparison.OrdinalIgnoreCase);

		static bool TryReadLine(ref ReadOnlySequence<byte> sequence, [NotNullWhen(true)] out string? str)
		{
			SequenceReader<byte> reader = new(sequence);

			if (reader.TryReadTo(out ReadOnlySequence<byte> headerBuffer, Newline))
			{
				sequence = sequence.Slice(reader.Consumed);
				str = Encoding.UTF8.GetString(headerBuffer);
				return true;
			}

			str = default;
			return false;
		}
	}

	/// <summary>
	/// 使用 UDP echo server 测试 SOCKS5 UDP ASSOCIATE (via IPacketOutbound)
	/// </summary>
	public static async ValueTask<bool> Socks5UdpAssociateAsync(
		Socks5OutboundOption option,
		string targetHost,
		ushort targetPort,
		CancellationToken cancellationToken = default)
	{
		Socks5Outbound outbound = new(option);
		ProxyDestination destination = new(Encoding.ASCII.GetBytes(targetHost), targetPort);

		await using IPacketConnection packetConnection = await outbound.CreatePacketConnectionAsync(cancellationToken);

		byte[] payload = new byte[64];
		Random.Shared.NextBytes(payload);

		await packetConnection.SendToAsync(payload, destination, cancellationToken);

		byte[] receiveBuffer = new byte[0x10000];
		PacketReceiveResult result = await packetConnection.ReceiveFromAsync(receiveBuffer, cancellationToken);

		return receiveBuffer.AsSpan(0, result.BytesReceived).SequenceEqual(payload)
			&& result.RemoteDestination.Port == destination.Port;
	}
}
