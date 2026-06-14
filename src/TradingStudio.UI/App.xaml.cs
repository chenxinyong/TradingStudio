using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using TradingStudio.UI.ViewModels;

namespace TradingStudio.UI;

/// <summary>
/// TradingStudio.UI — K线图表 + 监控面板。
/// 启动时初始化 Serilog + IConfiguration + DI 容器。
/// </summary>
public partial class App : Application
{
    /// <summary>DI 容器（全局可用）</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. 加载配置
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // 2. 初始化 Serilog
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(config)
            .CreateLogger();

        Log.Information("TradingStudio.UI starting...");

        // 3. DI 容器
        var services = new ServiceCollection();

        // 配置
        services.AddSingleton<IConfiguration>(config);

        // 日志 — 通过 Microsoft.Extensions.Logging 桥接 Serilog
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: true);
        });

        // ViewModels
        services.AddTransient<ChartViewModel>();

        Services = services.BuildServiceProvider();

        Log.Information("DI ready, {count} services registered", services.Count);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("TradingStudio.UI shutting down");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
