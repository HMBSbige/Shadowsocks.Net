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
using System;
using System.Net;
using System.Threading.Tasks;
using TestConsoleApp;

const string outputTemplate = @"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level}] {Message:lj}{NewLine}{Exception}";
Log.Logger = new LoggerConfiguration()
#if DEBUG
		.MinimumLevel.Debug()
#else
		.MinimumLevel.Information()
#endif
		.MinimumLevel.Override(@"Microsoft", LogEventLevel.Information)
		.MinimumLevel.Override(@"Volo.Abp", LogEventLevel.Warning)
		.Enrich.FromLogContext()
		.WriteTo.Async(c => c.Console(outputTemplate: outputTemplate))
		.CreateLogger();

var services = new ServiceCollection();
services.AddLogging(c => c.AddSerilog());
services.AddSingleton<IServersController, TestServersController>();
services.AddTransient<HttpService>();
services.AddTransient<Socks5Service>();
services.AddTransient<Socks5UdpService>();
services.AddTransient<HttpToSocks5>();

await using var provide = services.BuildServiceProvider();

const ushort port = 1080;
var local = new IPEndPoint(IPAddress.Loopback, port);
var httpService = provide.GetRequiredService<HttpService>();
var socks5Service = provide.GetRequiredService<Socks5Service>();
var socks5UdpService = provide.GetRequiredService<Socks5UdpService>();

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

var tcp = new TcpListenService(
	provide.GetRequiredService<ILogger<TcpListenService>>(),
	local,
	new ILocalTcpService[]
	{
		socks5Service,
		httpService
	});

var udp = new UdpListenService(
		provide.GetRequiredService<ILogger<UdpListenService>>(),
		local,
		new ILocalUdpService[]
		{
			socks5UdpService
		});

await Task.WhenAny(tcp.StartAsync().AsTask(), udp.StartAsync().AsTask());

Console.WriteLine(@"done");
