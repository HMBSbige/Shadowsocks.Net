using Microsoft;
using Pipelines.Extensions;
using Socks5.Enums;
using Socks5.Exceptions;
using Socks5.Models;
using Socks5.Utils;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Socks5.Clients
{
	public sealed class Socks5Client : IDisposable, IAsyncDisposable
	{
		#region Public Fields

		public Status Status { get; private set; } = Status.Initial;

		#endregion

		#region Private Fields

		private readonly TcpClient _tcpClient;
		private UdpClient? _udpClient;
		private readonly Socks5CreateOption _option;

		private IDuplexPipe? _pipe;

		#endregion

		#region Constructors

		public Socks5Client(Socks5CreateOption option)
		{
			Requires.NotNull(option, nameof(option));
			if (option.Address is null)
			{
				throw new ArgumentNullException(nameof(option.Address));
			}

			_option = option;
			_tcpClient = new TcpClient(option.Address.AddressFamily);
		}

		#endregion

		#region Connect

		public IDuplexPipe GetPipe()
		{
			if (Status is not Status.Established || _pipe is null)
			{
				throw new InvalidOperationException(@"Socks5 is not established.");
			}

			return _pipe;
		}

		public ValueTask<ServerBound> ConnectAsync(string dst, ushort dstPort, CancellationToken token = default)
		{
			return ConnectAsync(dst, null, dstPort, token);
		}

		public ValueTask<ServerBound> ConnectAsync(IPAddress dstAddress, ushort dstPort, CancellationToken token = default)
		{
			return ConnectAsync(null, dstAddress, dstPort, token);
		}

		private async ValueTask<ServerBound> ConnectAsync(string? dst, IPAddress? dstAddress, ushort dstPort, CancellationToken token = default)
		{
			var pipe = await HandshakeAsync(token);

			var bound = await SendCommandAsync(pipe, Command.Connect, dst, dstAddress, dstPort, token);

			_pipe = pipe;
			Status = Status.Established;

			return bound;
		}

		#endregion

		#region Udp

		public async ValueTask<ServerBound> UdpAssociateAsync(IPAddress address, ushort port = 0, CancellationToken token = default)
		{
			var pipe = await HandshakeAsync(token);

			_udpClient = new UdpClient(port, _option.Address!.AddressFamily);

			var bound = await SendCommandAsync(pipe, Command.UdpAssociate, default, address, port, token);

			switch (bound.Type)
			{
				case AddressType.IPv4:
				{
					if (Equals(bound.Address, IPAddress.Any))
					{
						bound.Address = _option.Address;
					}
					_udpClient.Connect(bound.Address!, bound.Port);
					break;
				}
				case AddressType.IPv6:
				{
					if (Equals(bound.Address, IPAddress.IPv6Any))
					{
						bound.Address = _option.Address;
					}
					_udpClient.Connect(bound.Address!, bound.Port);
					break;
				}
				case AddressType.Domain:
				{
					_udpClient.Connect(bound.Domain!, bound.Port);
					break;
				}
				default:
				{
					throw new ArgumentOutOfRangeException(nameof(bound.Type));
				}
			}

			Status = Status.Established;

			return bound;
		}

		//TODO .NET6.0
		public async Task<Socks5UdpReceivePacket> ReceiveAsync()
		{
			if (Status is not Status.Established || _udpClient is null)
			{
				throw new InvalidOperationException(@"Socks5 is not established.");
			}

			var res = await _udpClient.ReceiveAsync();

			return Unpack.Udp(res.Buffer);
		}

		public Task<int> SendUdpAsync(ReadOnlyMemory<byte> data, string dst, ushort dstPort)
		{
			return SendUdpAsync(data, dst, default, dstPort);
		}

		public Task<int> SendUdpAsync(ReadOnlyMemory<byte> data, IPAddress dstAddress, ushort dstPort)
		{
			return SendUdpAsync(data, default, dstAddress, dstPort);
		}

		private async Task<int> SendUdpAsync(
			ReadOnlyMemory<byte> data,
			string? dst, IPAddress? dstAddress, ushort dstPort)
		{
			if (Status is not Status.Established || _udpClient is null)
			{
				throw new InvalidOperationException(@"Socks5 is not established.");
			}

			var buffer = ArrayPool<byte>.Shared.Rent(Constants.MaxUdpHandshakeHeaderLength + data.Length);
			try
			{
				var length = Pack.Udp(buffer, dst, dstAddress, dstPort, data.Span);

				return await _udpClient.SendAsync(buffer, length);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}

		#endregion

		#region Private Methods

		private async ValueTask<IDuplexPipe> HandshakeAsync(CancellationToken token)
		{
			if (Status is not Status.Initial)
			{
				throw new InvalidOperationException(@"Socks5 already connected.");
			}

			await _tcpClient.ConnectAsync(_option.Address!, _option.Port, token);

			var pipe = _tcpClient.GetStream().AsDuplexPipe();

			await HandshakeWithAuthAsync(pipe, token);

			return pipe;
		}

		private async ValueTask HandshakeWithAuthAsync(IDuplexPipe pipe, CancellationToken token)
		{
			switch (Status)
			{
				case Status.Established:
				{
					throw new InvalidOperationException(@"Socks5 has been initialized.");
				}
				case Status.Closed:
				{
					throw new InvalidOperationException(@"Socks5 closed.");
				}
				case Status.Initial:
				{
					break;
				}
				default:
				{
					throw new ArgumentOutOfRangeException();
				}
			}

			var clientMethods = new List<Method>(2)
			{
				Method.NoAuthentication
			};
			if (_option.UsernamePassword is not null)
			{
				clientMethods.Add(Method.UsernamePassword);
			}

			var replyMethod = await HandshakeMethodAsync(pipe, clientMethods, token);
			switch (replyMethod)
			{
				case Method.NoAuthentication:
				{
					return;
				}
				case Method.UsernamePassword when _option.UsernamePassword is not null:
				{
					await AuthAsync(pipe, _option.UsernamePassword, token);
					break;
				}
				default:
				{
					throw new MethodUnsupportedException($@"Error method: {replyMethod}", replyMethod);
				}
			}
		}

		private static async ValueTask<Method> HandshakeMethodAsync(IDuplexPipe pipe, IReadOnlyList<Method> clientMethods, CancellationToken token)
		{
			await pipe.Output.WriteAsync(Constants.MaxHandshakeClientMethodLength, PackHandshake, token);

			// Receive

			var method = Method.NoAuthentication;

			await pipe.Input.ReadAsync(HandleResponse, token);

			if (!clientMethods.Contains(method))
			{
				throw new MethodUnsupportedException($@"Server sent an unsupported method ({method}:0x{(byte)method:X2}).", method);
			}

			return method;

			int PackHandshake(Memory<byte> memory)
			{
				return Pack.Handshake(clientMethods, memory.Span);
			}

			ParseResult HandleResponse(ref ReadOnlySequence<byte> buffer)
			{
				return Unpack.ReadResponseMethod(ref buffer, out method) ? ParseResult.Success : ParseResult.NeedsMoreData;
			}
		}

		private static async ValueTask AuthAsync(IDuplexPipe pipe, UsernamePassword credential, CancellationToken token)
		{
			await pipe.Output.WriteAsync(Constants.MaxUsernamePasswordAuthLength, PackUsernamePassword, token);

			// Receive

			await pipe.Input.ReadAsync(HandleResponse, token);

			int PackUsernamePassword(Memory<byte> memory)
			{
				return Pack.UsernamePasswordAuth(credential, memory.Span);
			}

			static ParseResult HandleResponse(ref ReadOnlySequence<byte> buffer)
			{
				return Unpack.ReadResponseAuthReply(ref buffer) ? ParseResult.Success : ParseResult.NeedsMoreData;
			}
		}

		private static async ValueTask<ServerBound> SendCommandAsync(
			IDuplexPipe pipe,
			Command command,
			string? dst, IPAddress? dstAddress, ushort dstPort,
			CancellationToken token)
		{
			await pipe.Output.WriteAsync(Constants.MaxCommandLength, PackClientCommand, token);

			// Receive

			var bound = new ServerBound();

			await pipe.Input.ReadAsync(HandleResponse, token);

			return bound;

			int PackClientCommand(Memory<byte> memory)
			{
				return Pack.ClientCommand(command, dst, dstAddress, dstPort, memory.Span);
			}

			ParseResult HandleResponse(ref ReadOnlySequence<byte> buffer)
			{
				return Unpack.ReadServerReplyCommand(ref buffer, out bound) ? ParseResult.Success : ParseResult.NeedsMoreData;
			}
		}

		#endregion

		#region Dispose

		private void DisposeSync()
		{
			Status = Status.Closed;
			_tcpClient.Dispose();
			_udpClient?.Dispose();
		}

		public void Dispose()
		{
			DisposeSync();

			if (_pipe is not null)
			{
				_pipe.Input.Complete();
				_pipe.Output.Complete();
			}
		}

		public async ValueTask DisposeAsync()
		{
			DisposeSync();

			if (_pipe is not null)
			{
				await _pipe.Input.CompleteAsync();
				await _pipe.Output.CompleteAsync();
			}
		}

		#endregion
	}
}
