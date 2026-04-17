using Pipelines.Extensions;
using Socks5.Protocol;
using System.IO.Pipelines;

namespace Socks5;

public static partial class Socks5Utils
{
	internal static readonly Method[] MethodsNoAuth = [Method.NoAuthentication];
	internal static readonly Method[] MethodsWithAuth = [Method.NoAuthentication, Method.UsernamePassword];

	internal static async ValueTask<Method> HandshakeMethodAsync(IDuplexPipe pipe, Method[] clientMethods, CancellationToken cancellationToken)
	{
		await pipe.Output.WriteAsync(
			Constants.MaxHandshakeClientMethodLength,
			clientMethods,
			static (methods, span) => Pack.Handshake(methods, span),
			cancellationToken);

		(bool ok, Method method) = await pipe.Input.ReadAsync<byte, Method>(
			0,
			static (_, out method, ref buf) =>
				Unpack.ReadResponseMethod(ref buf, out method) ? ParseResult.Success : ParseResult.NeedsMoreData,
			cancellationToken);

		if (!ok)
		{
			throw new InvalidDataException(@"Incomplete SOCKS5 method reply.");
		}

		if (!clientMethods.AsSpan().Contains(method))
		{
			throw new Socks5MethodUnsupportedException($@"Server sent an unsupported method ({method}:0x{(byte)method:X2}).", method);
		}

		return method;
	}

	internal static async ValueTask AuthAsync(IDuplexPipe pipe, UserPassAuth credential, CancellationToken cancellationToken)
	{
		await pipe.Output.WriteAsync(
			Constants.MaxUsernamePasswordAuthLength,
			credential,
			Pack.UsernamePasswordAuth,
			cancellationToken);

		bool ok = await pipe.Input.ReadAsync<byte>(
			0,
			static (_, ref buf) =>
				Unpack.ReadResponseAuthReply(ref buf) ? ParseResult.Success : ParseResult.NeedsMoreData,
			cancellationToken);

		if (!ok)
		{
			throw new InvalidDataException("Incomplete SOCKS5 auth reply.");
		}
	}

	internal static async ValueTask<ServerBound> SendCommandAsync(
		IDuplexPipe pipe, Command command,
		ReadOnlyMemory<byte> host, ushort port,
		CancellationToken cancellationToken)
	{
		await pipe.Output.WriteAsync(
			Constants.MaxCommandLength,
			(command, host, port),
			static (state, span) =>
				Pack.ClientCommand(state.command, state.host.Span, state.port, span),
			cancellationToken);

		(bool ok, ServerBound bound) = await pipe.Input.ReadAsync<byte, ServerBound>(
			0,
			static (_, out bound, ref buf) =>
				Unpack.ReadServerReplyCommand(ref buf, out bound) ? ParseResult.Success : ParseResult.NeedsMoreData,
			cancellationToken);

		if (!ok)
		{
			throw new InvalidDataException("Incomplete SOCKS5 command reply.");
		}

		return bound;
	}
}
