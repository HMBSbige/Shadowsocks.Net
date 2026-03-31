using Pipelines.Extensions;
using Socks5.Protocol;
using System.Buffers;
using System.IO.Pipelines;

namespace Socks5;

public static partial class Socks5Utils
{
	internal static readonly Method[] MethodsNoAuth = [Method.NoAuthentication];
	internal static readonly Method[] MethodsWithAuth = [Method.NoAuthentication, Method.UsernamePassword];
	internal static readonly byte[] IPv4Unspecified = "0.0.0.0"u8.ToArray();
	internal static readonly byte[] IPv6Unspecified = "::"u8.ToArray();

	internal static async ValueTask<Method> HandshakeMethodAsync(IDuplexPipe pipe, Method[] clientMethods, CancellationToken cancellationToken)
	{
		await pipe.Output.WriteAsync(Constants.MaxHandshakeClientMethodLength, PackHandshake, cancellationToken);

		Method method = Method.NoAuthentication;
		await pipe.Input.ReadAsync(HandleResponse, cancellationToken);

		bool found = false;

		foreach (Method m in clientMethods)
		{
			if (m == method)
			{
				found = true;
				break;
			}
		}

		if (!found)
		{
			throw new MethodUnsupportedException($@"Server sent an unsupported method ({method}:0x{(byte)method:X2}).", method);
		}

		return method;

		int PackHandshake(Span<byte> span)
		{
			return Pack.Handshake(clientMethods, span);
		}

		ParseResult HandleResponse(ref ReadOnlySequence<byte> buffer)
		{
			return Unpack.ReadResponseMethod(ref buffer, out method) ? ParseResult.Success : ParseResult.NeedsMoreData;
		}
	}

	internal static async ValueTask AuthAsync(IDuplexPipe pipe, UserPassAuth credential, CancellationToken cancellationToken)
	{
		await pipe.Output.WriteAsync(Constants.MaxUsernamePasswordAuthLength, PackUsernamePassword, cancellationToken);

		if (!await pipe.Input.ReadAsync(HandleResponse, cancellationToken))
		{
			throw new Socks5ProtocolErrorException(@"Auth failed!", Socks5Reply.ConnectionNotAllowed);
		}

		return;

		int PackUsernamePassword(Span<byte> span)
		{
			return Pack.UsernamePasswordAuth(credential, span);
		}

		static ParseResult HandleResponse(ref ReadOnlySequence<byte> buffer)
		{
			return Unpack.ReadResponseAuthReply(ref buffer) ? ParseResult.Success : ParseResult.NeedsMoreData;
		}
	}

	internal static async ValueTask<ServerBound> SendCommandAsync(
		IDuplexPipe pipe, Command command,
		ReadOnlyMemory<byte> host, ushort port,
		CancellationToken cancellationToken)
	{
		await pipe.Output.WriteAsync(Constants.MaxCommandLength, PackClientCommand, cancellationToken);

		ServerBound bound = new();

		if (!await pipe.Input.ReadAsync(HandleResponse, cancellationToken))
		{
			throw new Socks5ProtocolErrorException(@"Send command failed!", Socks5Reply.CommandNotSupported);
		}

		return bound;

		int PackClientCommand(Span<byte> span)
		{
			return Pack.ClientCommand(command, host.Span, port, span);
		}

		ParseResult HandleResponse(ref ReadOnlySequence<byte> buffer)
		{
			return Unpack.ReadServerReplyCommand(ref buffer, out bound) ? ParseResult.Success : ParseResult.NeedsMoreData;
		}
	}
}
