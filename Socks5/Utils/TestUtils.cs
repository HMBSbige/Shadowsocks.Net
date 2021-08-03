using Pipelines.Extensions;
using Socks5.Clients;
using Socks5.Enums;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Socks5.Utils
{
	public static class TestUtils
	{
		private static ReadOnlySpan<byte> HttpHeaderEnd => new[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };

		/// <summary>
		/// 使用 HTTP1.1 204 测试 SOCKS5 CONNECT
		/// </summary>
		public static async ValueTask<bool> Socks5ConnectAsync(
			IPEndPoint ipEndPoint,
			NetworkCredential? credential = null,
			string target = @"http://www.google.com/generate_204",
			string targetHost = @"www.google.com",
			ushort targetPort = 80,
			CancellationToken token = default)
		{
			var sendString = $"GET {target} HTTP/1.1\r\nHost: {targetHost}\r\n\r\n";

			await using var client = new Socks5Client(ipEndPoint, credential);

			var bound = await client.ConnectAsync(targetHost, targetPort, token);

			Debug.WriteLine($@"TCP: Supported, {bound.Type} {(bound.Type == AddressType.Domain ? bound.Domain : bound.Address)}:{bound.Port}");

			var pipe = client.GetPipe();

			await pipe.Output.WriteAsync(sendString, token);

			// Response

			var content = string.Empty;
			var success = await pipe.Input.ReadAsync(TryReadHttpHeaderEnd, token);

			Debug.WriteLine(success ? @"Success" : @"Failed");
			Debug.WriteLine($@"Receive: {Environment.NewLine}{content}");

			return content.Contains(@"HTTP/1.1 204 No Content");

			ParseResult TryReadHttpHeaderEnd(ref ReadOnlySequence<byte> buffer)
			{
				var reader = new SequenceReader<byte>(buffer);
				try
				{
					if (!reader.TryReadTo(out ReadOnlySequence<byte> contentBuffer, HttpHeaderEnd))
					{
						return ParseResult.NeedsMoreData;
					}

					content = Encoding.UTF8.GetString(contentBuffer);
					return ParseResult.Success;
				}
				finally
				{
					buffer = buffer.Slice(reader.Consumed);
				}
			}
		}

		/// <summary>
		/// 使用 DNS 测试 SOCKS5 UDP
		/// </summary>
		public static async ValueTask<bool> Socks5UdpAssociateAsync(
			IPEndPoint ipEndPoint,
			string target = @"bing.com",
			string targetHost = @"8.8.8.8",
			ushort targetPort = 53,
			CancellationToken token = default)
		{
			await using var client = new Socks5Client(ipEndPoint);
			var bound = await client.UdpAssociateAsync(IPAddress.Any, 0, token);

			Debug.WriteLine($@"UDP: Supported, {bound.Type} {(bound.Type == AddressType.Domain ? bound.Domain : bound.Address)}:{bound.Port}");

			var buffer = ArrayPool<byte>.Shared.Rent(ushort.MaxValue + 1);
			try
			{
				RandomNumberGenerator.Fill(buffer.AsSpan(0, 2));
				buffer[2] = 0x01;
				buffer[3] = 0x00;
				buffer[4] = 0x00;
				buffer[5] = 0x01;
				buffer[6] = 0x00;
				buffer[7] = 0x00;
				buffer[8] = 0x00;
				buffer[9] = 0x00;
				buffer[10] = 0x00;
				buffer[11] = 0x00;
				var offset = 12;
				offset += Pack.DnsDomain(target, buffer.AsSpan(12));
				buffer[offset++] = 0x00;
				buffer[offset++] = 0x01;
				buffer[offset++] = 0x00;
				buffer[offset++] = 0x01;

				await client.SendUdpAsync(buffer.AsMemory(0, offset), targetHost, targetPort);

				var res = await client.ReceiveAsync();

				return res.Data.Span[..2].SequenceEqual(buffer.AsSpan(0, 2));
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}
	}
}
