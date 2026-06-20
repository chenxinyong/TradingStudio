using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TradingStudio.Terminal.Startup;

namespace TradingStudio.Terminal;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Services = Bootstrapper.ConfigureServices();

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Serilog.Log.Information("TradingStudio.Terminal shutting down");
        Serilog.Log.CloseAndFlush();
        (Services as IDisposable)?.Dispose();
        base.OnExit(e);
    }
}
