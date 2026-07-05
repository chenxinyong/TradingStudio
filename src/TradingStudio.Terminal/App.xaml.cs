using System.IO;
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

        try
        {
            Services = Bootstrapper.ConfigureServices();
            var args = e.Args;

            if (args.Contains("--replay"))
                Services.GetRequiredService<ReplayWindow>().Show();
            else if (args.Contains("--chart-only") || args.Contains("--chart") || args.Length == 0)
                Services.GetRequiredService<ChartWindow>().Show();
            else
                Services.GetRequiredService<MainWindow>().Show();
        }
        catch (Exception ex)
        {
            File.WriteAllText("wpf_crash.log", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            MessageBox.Show($"FATAL: {ex.Message}", "Crash", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Serilog.Log.Information("TradingStudio.Terminal shutting down");
        Serilog.Log.CloseAndFlush();
        (Services as IDisposable)?.Dispose();
        base.OnExit(e);
    }
}
