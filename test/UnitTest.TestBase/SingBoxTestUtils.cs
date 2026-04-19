using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace UnitTest.TestBase;

public static class SingBoxTestUtils
{
	public const string SkipReason = "sing-box 不在 PATH 中，跳过互操作测试。";

	private const string LoopbackHost = "127.0.0.1";
	private const int CheckAvailableTimeoutMilliseconds = 5_000;
	internal static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(5);
	internal static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(5);
	internal static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(50);
	private static readonly Regex StartedTcpServerRegex = new($@"tcp server started at {Regex.Escape(LoopbackHost)}:(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Lazy<bool> _isAvailable = new(CheckAvailable);

	public static bool IsAvailable => _isAvailable.Value;

	public static Task<SingBoxInstance> StartHttpInboundAsync(SingBoxProtocol outboundProtocol, ushort upstreamPort, CancellationToken cancellationToken)
	{
		return StartAsync(SingBoxProtocol.Http, new Upstream(outboundProtocol, upstreamPort), cancellationToken);
	}

	public static Task<SingBoxInstance> StartSocksInboundAsync(CancellationToken cancellationToken)
	{
		return StartAsync(SingBoxProtocol.Socks, null, cancellationToken);
	}

	internal static void TryDeleteFile(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	internal static void TryKill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
		}
		catch (Win32Exception)
		{
		}
	}

	private static async Task<SingBoxInstance> StartAsync(SingBoxProtocol inboundProtocol, Upstream? upstream, CancellationToken cancellationToken)
	{
		if (!IsAvailable)
		{
			throw new InvalidOperationException(SkipReason);
		}

		string configJson = BuildConfigJson(inboundProtocol, upstream);
		string configPath = CreateConfigPath();

		await File.WriteAllTextAsync(configPath, configJson, cancellationToken);

		ProcessOutputCapture stdout = new();
		ProcessOutputCapture stderr = new();
		Process process;

		try
		{
			process = StartProcess(configPath, stdout, stderr);
		}
		catch
		{
			TryDeleteFile(configPath);
			throw;
		}

		SingBoxInstance instance = new(process, configPath, configJson, stdout, stderr);

		try
		{
			await instance.WaitForReadyAsync(cancellationToken);
			return instance;
		}
		catch
		{
			await instance.DisposeAsync();
			throw;
		}
	}

	private static string BuildConfigJson(SingBoxProtocol inboundProtocol, Upstream? upstream)
	{
		string inboundType = ToJsonName(inboundProtocol);
		string outboundJson = BuildOutboundJson(upstream);

		return $$"""
				{
				  "log": { "level": "info" },
				  "inbounds": [
				    { "type": "{{inboundType}}", "tag": "local-{{inboundType}}", "listen": "{{LoopbackHost}}", "listen_port": 0 }
				  ],
				  "outbounds": [
				    {{outboundJson}}
				  ]
				}
				""";
	}

	private static string BuildOutboundJson(Upstream? upstream)
	{
		if (upstream is null)
		{
			return """
					{
					  "type": "direct",
					  "tag": "direct"
					}
					""";
		}

		string outboundType = ToJsonName(upstream.Protocol);
		return $$"""
				{
				  "type": "{{outboundType}}",
				  "tag": "upstream-{{outboundType}}",
				  "server": "{{LoopbackHost}}",
				  "server_port": {{upstream.Port}}
				}
				""";
	}

	private static string ToJsonName(SingBoxProtocol protocol)
	{
		return protocol switch
		{
			SingBoxProtocol.Http => "http",
			SingBoxProtocol.Socks => "socks",
			_ => throw new ArgumentOutOfRangeException(nameof(protocol)),
		};
	}

	private static string CreateConfigPath()
	{
		return Path.Combine(Path.GetTempPath(), $"sing-box-interop-{Guid.CreateVersion7(DateTimeOffset.Now):N}.json");
	}

	private static bool CheckAvailable()
	{
		using Process process = CreateProcess("version");

		try
		{
			if (!process.Start())
			{
				return false;
			}

			if (!process.WaitForExit(CheckAvailableTimeoutMilliseconds))
			{
				return false;
			}

			return process.ExitCode is 0;
		}
		catch (Exception)
		{
			return false;
		}
		finally
		{
			TryKill(process);
		}
	}

	private static Process CreateProcess(params string[] arguments)
	{
		Process process = new()
		{
			StartInfo =
			{
				FileName = "sing-box",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			},
		};

		foreach (string argument in arguments)
		{
			process.StartInfo.ArgumentList.Add(argument);
		}

		return process;
	}

	private static Process StartProcess(string configPath, ProcessOutputCapture stdout, ProcessOutputCapture stderr)
	{
		Process process = CreateProcess("--disable-color", "run", "-c", configPath);

		process.OutputDataReceived += (_, e) => stdout.Append(e.Data);
		process.ErrorDataReceived += (_, e) => stderr.Append(e.Data);

		try
		{
			if (!process.Start())
			{
				throw new InvalidOperationException("无法启动 sing-box 进程。");
			}

			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			return process;
		}
		catch
		{
			TryKill(process);
			process.Dispose();
			throw;
		}
	}

	internal static ushort? TryGetStartedPort(string output)
	{
		Match match = StartedTcpServerRegex.Match(output);

		if (!match.Success)
		{
			return null;
		}

		return ushort.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
	}

	private sealed record Upstream(SingBoxProtocol Protocol, ushort Port);
}

public enum SingBoxProtocol
{
	Http,
	Socks,
}

internal sealed class ProcessOutputCapture
{
	private readonly Lock _syncRoot = new();
	private readonly StringBuilder _buffer = new();

	public void Append(string? line)
	{
		if (line is null)
		{
			return;
		}

		lock (_syncRoot)
		{
			_buffer.AppendLine(line);
		}
	}

	public string Snapshot()
	{
		lock (_syncRoot)
		{
			return _buffer.ToString();
		}
	}
}

public sealed class SingBoxInstance : IAsyncDisposable
{
	private readonly Process _process;
	private readonly string _configPath;
	private readonly ProcessOutputCapture _stdout;
	private readonly ProcessOutputCapture _stderr;

	public ushort Port { get; private set; }

	private string ConfigJson { get; }
	private string StandardOutput => _stdout.Snapshot();
	private string StandardError => _stderr.Snapshot();

	internal SingBoxInstance(Process process, string configPath, string configJson, ProcessOutputCapture stdout, ProcessOutputCapture stderr)
	{
		_process = process;
		_configPath = configPath;
		ConfigJson = configJson;
		_stdout = stdout;
		_stderr = stderr;
	}

	public async ValueTask DisposeAsync()
	{
		SingBoxTestUtils.TryKill(_process);

		try
		{
			using CancellationTokenSource timeout = new(SingBoxTestUtils.ProcessExitTimeout);
			await _process.WaitForExitAsync(timeout.Token);
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			_process.Dispose();
			SingBoxTestUtils.TryDeleteFile(_configPath);
		}
	}

	internal string GetDebugInfo()
	{
		return $"""

				Config:
				{ConfigJson}

				stdout:
				{StandardOutput}

				stderr:
				{StandardError}
				""";
	}

	internal async Task WaitForReadyAsync(CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(SingBoxTestUtils.ReadyTimeout);

		try
		{
			while (true)
			{
				if (_process.HasExited)
				{
					throw new InvalidOperationException($"sing-box 进程提前退出。{GetDebugInfo()}");
				}

				ushort? startedPort = SingBoxTestUtils.TryGetStartedPort(StandardError);

				if (startedPort is { } port && await TryConnectAsync(port, timeoutCts.Token))
				{
					Port = port;
					return;
				}

				await Task.Delay(SingBoxTestUtils.ReadyPollInterval, timeoutCts.Token);
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException($"等待 sing-box 输出监听端口并就绪超时。{GetDebugInfo()}");
		}
	}

	private static async Task<bool> TryConnectAsync(ushort port, CancellationToken cancellationToken)
	{
		try
		{
			using TcpClient tcp = new();
			await tcp.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
			return true;
		}
		catch (SocketException)
		{
			return false;
		}
	}
}
