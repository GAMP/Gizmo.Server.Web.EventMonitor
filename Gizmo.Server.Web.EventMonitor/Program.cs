using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gizmo.Server.Web.EventMonitor
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            IHost host = Host.CreateDefaultBuilder(args)
                .ConfigureHostConfiguration(c =>
                {
                    c.AddJsonFile("Settings.json");
                })
                .ConfigureLogging(l =>
                {
                    l.AddConsole();
                    l.SetMinimumLevel(LogLevel.Trace);
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddOptions<RealTimeConfig>().BindConfiguration("RealTime");
                    services.AddOptions<AuthenticationConfig>().BindConfiguration("Authentication");
                    services.AddHostedService<RealTimeService>();
                })
                .Build();

            await host.RunAsync();
        }
    }
}
