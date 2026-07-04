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

        // ── 启动模式判断 ──
        var args = e.Args;
        bool chartOnly = args.Contains("--chart-only") || args.Contains("--chart");

        if (chartOnly)
        {
            // 轻量模式：仅 K 线图，无仪表盘/侧栏/命令栏
            var chartWindow = Services.GetRequiredService<ChartWindow>();
            chartWindow.Show();
        }
        else
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
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
