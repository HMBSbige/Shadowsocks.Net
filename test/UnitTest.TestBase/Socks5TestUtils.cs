using Pipelines.Extensions;
using Socks5.Clients;
using Socks5.Enums;
using Socks5.Models;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace UnitTest.TestBase;

public static class Socks5TestUtils
{
	private static ReadOnlySpan<byte> Newline => "\r\n"u8;

	/// <summary>
	/// 使用 HTTP1.1 204 测试 SOCKS5 CONNECT
	/// </summary>
	public static async ValueTask<bool> Socks5ConnectAsync(
		Socks5CreateOption option,
		string target,
		string targetHost,
		ushort targetPort,
		CancellationToken cancellationToken = default)
	{
		string sendString = $"GET {target} HTTP/1.1\r\nHost: {targetHost}\r\n\r\n";

		using Socks5Client client = new(option);

		ServerBound bound = await client.ConnectAsync(targetHost, targetPort, cancellationToken);

		Debug.WriteLine($@"TCP: Supported, {bound.Type} {(bound.Type is AddressType.Domain ? bound.Domain : bound.Address)}:{bound.Port}");

		IDuplexPipe pipe = client.GetPipe();

		pipe.Output.Write(sendString);
		await pipe.Output.FlushAsync(cancellationToken);

		// Response

		string? content;
		while (true)
		{
			ReadResult result = await pipe.Input.ReadAsync(cancellationToken);
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
				pipe.Input.AdvanceTo(buffer.Start, buffer.End);
			}
		}

		return content is not null && content.Equals(@"HTTP/1.1 204 No Content", StringComparison.OrdinalIgnoreCase);

		static bool TryReadLine(ref ReadOnlySequence<byte> sequence, [NotNullWhen(true)] out string? str)
		{
			SequenceReader<byte> reader = new(sequence);
			if (reader.TryReadTo(out ReadOnlySequence<byte> headerBuffer, Newline))
			{
				sequence = sequence.Slice(reader.Consumed);
				str = Encoding.UTF8.GetString(headerBuffer); // 不包括结尾的 \r\n
				return true;
			}

			str = default;
			return false;
		}
	}

	/// <summary>
	/// 使用本地 UDP echo server 测试 SOCKS5 UDP ASSOCIATE
	/// </summary>
	public static async ValueTask<bool> Socks5UdpAssociateAsync(
		Socks5CreateOption option,
		string targetHost,
		ushort targetPort,
		CancellationToken cancellationToken = default)
	{
		using Socks5Client client = new(option);

		ServerBound bound = await client.UdpAssociateAsync(IPAddress.Any, 0, cancellationToken);

		Debug.WriteLine($@"UDP: Supported, {bound.Type} {(bound.Type is AddressType.Domain ? bound.Domain : bound.Address)}:{bound.Port}");

		byte[] payload = new byte[64];
		RandomNumberGenerator.Fill(payload);

		await client.SendUdpAsync(payload, targetHost, targetPort, cancellationToken);

		Socks5UdpReceivePacket res = await client.ReceiveAsync(cancellationToken);

		return res.Data.Span.SequenceEqual(payload);
	}
}
