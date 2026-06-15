using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using TradingStudio.Terminal.Services;
using TradingStudio.Terminal.ViewModels;

namespace TradingStudio.Terminal;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(config)
            .CreateLogger();

        Log.Information("TradingStudio.Terminal starting...");

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => { b.ClearProviders(); b.AddSerilog(Log.Logger, dispose: true); });

        // MVVM
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

        // Engine connection
        services.AddSingleton<EngineHubClient>();

        // ViewModels
        services.AddTransient<ChartViewModel>();
        services.AddTransient<DashboardViewModel>();

        Services = services.BuildServiceProvider();
        Log.Information("DI ready, {count} services", services.Count);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("TradingStudio.Terminal shutting down");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
