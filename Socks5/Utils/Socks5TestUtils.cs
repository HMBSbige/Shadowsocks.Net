using Pipelines.Extensions;
using Socks5.Clients;
using Socks5.Enums;
using Socks5.Models;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Socks5.Utils
{
	public static class Socks5TestUtils
	{
		private static ReadOnlySpan<byte> Newline => new[] { (byte)'\r', (byte)'\n' };

		/// <summary>
		/// 使用 HTTP1.1 204 测试 SOCKS5 CONNECT 
		/// <para>Example:</para>
		/// <para>https://www.google.com/generate_204</para>
		/// <para>http://connectivitycheck.gstatic.com/generate_204</para>
		/// <para>http://connect.rom.miui.com/generate_204</para>
		/// <para>http://cp.cloudflare.com</para>
		/// </summary>
		public static async ValueTask<bool> Socks5ConnectAsync(
			Socks5CreateOption option,
			string target = @"http://cp.cloudflare.com",
			string targetHost = @"cp.cloudflare.com",
			ushort targetPort = 80,
			CancellationToken token = default)
		{
			var sendString = $"GET {target} HTTP/1.1\r\nHost: {targetHost}\r\n\r\n";

			await using var client = new Socks5Client(option);

			var bound = await client.ConnectAsync(targetHost, targetPort, token);

			Debug.WriteLine($@"TCP: Supported, {bound.Type} {(bound.Type == AddressType.Domain ? bound.Domain : bound.Address)}:{bound.Port}");

			var pipe = client.GetPipe();

			await pipe.Output.WriteAsync(sendString, token);

			// Response

			string? content;
			while (true)
			{
				var result = await pipe.Input.ReadAsync(token);
				var buffer = result.Buffer;
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
				var reader = new SequenceReader<byte>(sequence);
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
		/// 使用 DNS 测试 SOCKS5 UDP
		/// </summary>
		public static async ValueTask<bool> Socks5UdpAssociateAsync(
			Socks5CreateOption option,
			string target = @"bing.com",
			string targetHost = @"8.8.8.8",
			ushort targetPort = 53,
			CancellationToken token = default)
		{
			await using var client = new Socks5Client(option);

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
