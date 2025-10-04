using Pipelines.Extensions;
using Socks5.Enums;
using Socks5.Exceptions;
using Socks5.Models;
using Socks5.Utils;
using System.Buffers;
using System.IO.Pipelines;

namespace Socks5.Servers;

public sealed class Socks5ServerConnection
{
	private readonly IDuplexPipe _pipe;
	private readonly UsernamePassword? _credential;

	public ServerBound Target;

	public Command Command { get; private set; }

	public Socks5ServerConnection(IDuplexPipe pipe, UsernamePassword? credential = null)
	{
		_pipe = pipe;
		_credential = credential;
	}

	public static bool IsClientHeader(ReadOnlySequence<byte> buffer)
	{
		// +----+----------+----------+
		// |VER | NMETHODS | METHODS  |
		// +----+----------+----------+
		// | 1  |    1     | 1 to 255 |
		// +----+----------+----------+

		SequenceReader<byte> reader = new(buffer);
		if (!reader.TryRead(out byte ver))
		{
			return false;
		}

		if (ver is not Constants.ProtocolVersion)
		{
			return false;
		}

		if (!reader.TryRead(out byte num) || num <= 0)
		{
			return false;
		}

		if (reader.Remaining < num)
		{
			return false;
		}

		return true;
	}

	public async ValueTask AcceptClientAsync(CancellationToken token = default)
	{
		HashSet<Method> methods = new();

		await _pipe.Input.ReadAsync(TryReadClientHandshake, token);

		if (methods.Count <= 0)
		{
			throw new InvalidDataException(@"Error SOCKS5 header!");
		}

		// Select method
		Method method = Method.NoAcceptable;
		if (_credential is not null && !string.IsNullOrEmpty(_credential.UserName))
		{
			method = Method.UsernamePassword;
		}
		else if (methods.Contains(Method.NoAuthentication))
		{
			method = Method.NoAuthentication;
		}

		// Send method to client
		await _pipe.Output.WriteAsync(2, PackMethod, token);

		if (method is Method.UsernamePassword && !await UsernamePasswordAuthAsync(token))
		{
			throw new Socks5ProtocolErrorException(@"SOCKS5 auth username password error.", Socks5Reply.ConnectionNotAllowed);
		}

		if (method is not Method.NoAcceptable)
		{
			await ReadTargetAsync(token);
		}

		ParseResult TryReadClientHandshake(ref ReadOnlySequence<byte> buffer)
		{
			return Unpack.ReadClientHandshake(ref buffer, ref methods) ? ParseResult.Success : ParseResult.NeedsMoreData;
		}

		int PackMethod(Span<byte> span)
		{
			return Pack.Handshake(method, span);
		}
	}

	private async ValueTask<bool> UsernamePasswordAuthAsync(CancellationToken token = default)
	{
		UsernamePassword? clientCredential = null;
		await _pipe.Input.ReadAsync(TryReadClientAuth, token);

		bool isAuth = clientCredential == _credential;

		await _pipe.Output.WriteAsync(2, PackReply, token);

		return isAuth;

		ParseResult TryReadClientAuth(ref ReadOnlySequence<byte> buffer)
		{
			return Unpack.ReadClientAuth(ref buffer, ref clientCredential) ? ParseResult.Success : ParseResult.NeedsMoreData;
		}

		int PackReply(Span<byte> span)
		{
			return Pack.AuthReply(isAuth, span);
		}
	}

	private async ValueTask ReadTargetAsync(CancellationToken token = default)
	{
		await _pipe.Input.ReadAsync(TryReadCommand, token);

		ParseResult TryReadCommand(ref ReadOnlySequence<byte> buffer)
		{
			// +----+-----+-------+------+----------+----------+
			// |VER | CMD |  RSV  | ATYP | DST.ADDR | DST.PORT |
			// +----+-----+-------+------+----------+----------+
			// | 1  |  1  | X'00' |  1   | Variable |    2     |
			// +----+-----+-------+------+----------+----------+

			SequenceReader<byte> reader = new(buffer);

			if (!reader.TryRead(out byte ver))
			{
				return ParseResult.NeedsMoreData;
			}
			if (ver is not Constants.ProtocolVersion)
			{
				throw new Socks5ProtocolErrorException($@"client version is not 0x05: 0x{ver:X2}.", Socks5Reply.GeneralFailure);
			}

			if (!reader.TryRead(out byte cmd))
			{
				return ParseResult.NeedsMoreData;
			}

			Command = (Command)cmd;
			if (!Enum.IsDefined(typeof(Command), Command))
			{
				throw new Socks5ProtocolErrorException($@"client sent an unknown command: {Command}.", Socks5Reply.CommandNotSupported);
			}

			if (!reader.TryRead(out byte rsv))
			{
				return ParseResult.NeedsMoreData;
			}
			if (rsv is not Constants.Rsv)
			{
				throw new Socks5ProtocolErrorException($@"Protocol failed, RESERVED is not 0x00: 0x{rsv:X2}.", Socks5Reply.GeneralFailure);
			}

			if (!reader.TryRead(out byte type))
			{
				return ParseResult.NeedsMoreData;
			}

			Target.Type = (AddressType)type;
			if (!reader.ReadDestinationAddress(Target.Type, out Target.Address, out Target.Domain))
			{
				return ParseResult.NeedsMoreData;
			}

			if (!reader.TryReadBigEndian(out short port))
			{
				return ParseResult.NeedsMoreData;
			}
			Target.Port = (ushort)port;

			buffer = buffer.Slice(reader.Consumed);
			return ParseResult.Success;
		}
	}

	public async ValueTask SendReplyAsync(Socks5Reply reply, ServerBound bound, CancellationToken token = default)
	{
		await _pipe.Output.WriteAsync(Constants.MaxCommandLength, PackCommand, token);

		int PackCommand(Span<byte> span)
		{
			return Pack.ServerReply(reply, bound, span);
		}
	}
}
