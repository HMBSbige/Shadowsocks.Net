using HttpProxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Shadowsocks.Protocol.ListenServices;
using Shadowsocks.Protocol.LocalTcpServices;
using Shadowsocks.Protocol.LocalUdpServices;
using Shadowsocks.Protocol.ServersControllers;
using Socks5.Models;
using System.Net;
using TestConsoleApp;

const string outputTemplate = @"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";
Log.Logger = new LoggerConfiguration()
#if DEBUG
	.MinimumLevel.Debug()
#else
	.MinimumLevel.Information()
#endif
	.MinimumLevel.Override(@"Microsoft", LogEventLevel.Information)
	.MinimumLevel.Override(@"Volo.Abp", LogEventLevel.Warning)
	.Enrich.FromLogContext()
	.Enrich.With<SourceContextToClassNameEnricher>()
	.WriteTo.Async(c => c.Console(outputTemplate: outputTemplate))
	.CreateLogger();

ServiceCollection services = new();
services.AddLogging(c => c.AddSerilog());
services.AddSingleton<IServersController, TestServersController>();
services.AddTransient<HttpService>();
services.AddTransient<Socks5Service>();
services.AddTransient<Socks5UdpService>();
services.AddTransient<HttpToSocks5>();
services.AddMemoryCache();

await using ServiceProvider provide = services.BuildServiceProvider();

const ushort port = 1080;
IPEndPoint local = new(IPAddress.Loopback, port);
HttpService httpService = provide.GetRequiredService<HttpService>();
Socks5Service socks5Service = provide.GetRequiredService<Socks5Service>();
Socks5UdpService socks5UdpService = provide.GetRequiredService<Socks5UdpService>();

httpService.Socks5CreateOption = new Socks5CreateOption
{
	Address = IPAddress.Loopback,
	Port = port
};
socks5Service.Socks5CreateOption = new Socks5CreateOption
{
	Address = IPAddress.Loopback,
	Port = port
};

TcpListenService tcp = new(
	provide.GetRequiredService<ILogger<TcpListenService>>(),
	local,
	new ILocalTcpService[]
	{
		socks5Service,
		httpService
	}
);

UdpListenService udp = new(
	provide.GetRequiredService<ILogger<UdpListenService>>(),
	local,
	new ILocalUdpService[]
	{
		socks5UdpService
	}
);

await Task.WhenAny(tcp.StartAsync().AsTask(), udp.StartAsync().AsTask());

Console.WriteLine(@"done");
