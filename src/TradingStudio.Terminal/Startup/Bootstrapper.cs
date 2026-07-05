using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using TradingStudio.Terminal.Core.Commands;
using TradingStudio.Terminal.Core.Input;
using TradingStudio.Terminal.Core.Messaging;
using TradingStudio.Terminal.Panels;
using TradingStudio.Terminal.Services;
using TradingStudio.Terminal.ViewModels;

namespace TradingStudio.Terminal.Startup;

/// <summary>
/// DI assembly — all services registered in one place.
/// Pattern borrowed from CodeEditor's Editor.App.Startup.Bootstrapper.
/// </summary>
public static class Bootstrapper
{
    public static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // ── Configuration ──
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
        services.AddSingleton<IConfiguration>(config);

        // ── Logging ──
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(config)
            .CreateLogger();
        services.AddLogging(b => { b.ClearProviders(); b.AddSerilog(Log.Logger, dispose: true); });

        // ── Core — EventBus + Command System ──
        services.AddSingleton<EventBus>();
        services.AddSingleton<CommandRegistry>();
        services.AddSingleton<KeybindingRegistry>();

        // ── Engine Connection ──
        var engineUrl = config["Engine:Url"] ?? "http://localhost:5001";
        services.AddSingleton<EngineApiClient>(sp =>
        {
            var log = sp.GetRequiredService<ILogger<EngineApiClient>>();
            return new EngineApiClient(engineUrl, log);
        });
        services.AddSingleton<EngineHubClient>(sp =>
        {
            var log = sp.GetRequiredService<ILogger<EngineHubClient>>();
            var eventBus = sp.GetRequiredService<EventBus>();
            return new EngineHubClient($"{engineUrl}/hubs/engine", log, eventBus);
        });

        // ── Navigation ──
        services.AddSingleton<INavigationService, NavigationService>();

        // ── ViewModels ──
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<ChartViewModel>();
        services.AddTransient<BacktestViewModel>();

        // ── Panel System ──
        services.AddSingleton<PanelManager>();
        services.AddSingleton<ActivityBarViewModel>();

        // ── UI Windows ──
        services.AddSingleton<MainWindow>();
        services.AddSingleton<ChartWindow>();

        var provider = services.BuildServiceProvider();

        // ── Post-build initialization ──
        Log.Information("TradingStudio Terminal — DI ready");
        InitializeCommands(provider);
        InitializePanels(provider);

        return provider;
    }

    private static void InitializeCommands(IServiceProvider sp)
    {
        var registry = sp.GetRequiredService<CommandRegistry>();
        var nav = sp.GetRequiredService<INavigationService>();
        var hub = sp.GetRequiredService<EngineHubClient>();

        // ── View Commands ──
        registry.Register("view.dashboard", () =>
        {
            Application.Current.Dispatcher.Invoke(() =>
                GetMainWindow(sp).SwitchToPanel("dashboard"));
        }, cmd =>
        {
            cmd.Title = "仪表盘"; cmd.Category = "视图";
            cmd.DefaultGesture = new KeyGesture(Key.D, ModifierKeys.Control);
        });

        registry.Register("view.chart", () =>
        {
            Application.Current.Dispatcher.Invoke(() =>
                GetMainWindow(sp).SwitchToPanel("chart"));
        }, cmd =>
        {
            cmd.Title = "K线图"; cmd.Category = "视图";
            cmd.DefaultGesture = new KeyGesture(Key.C, ModifierKeys.Control);
        });

        registry.Register("view.strategies", () =>
        {
            Application.Current.Dispatcher.Invoke(() =>
                GetMainWindow(sp).SwitchToPanel("strategies"));
        }, cmd =>
        {
            cmd.Title = "策略列表"; cmd.Category = "视图";
            cmd.DefaultGesture = new KeyGesture(Key.S, ModifierKeys.Control);
        });

        registry.Register("view.orders", () =>
        {
            Application.Current.Dispatcher.Invoke(() =>
                GetMainWindow(sp).SwitchToPanel("orders"));
        }, cmd =>
        {
            cmd.Title = "订单历史"; cmd.Category = "视图";
            cmd.DefaultGesture = new KeyGesture(Key.O, ModifierKeys.Control);
        });

        registry.Register("view.backtest", () =>
        {
            Application.Current.Dispatcher.Invoke(() =>
                GetMainWindow(sp).SwitchToPanel("backtest"));
        }, cmd =>
        {
            cmd.Title = "回测"; cmd.Category = "视图";
            cmd.DefaultGesture = new KeyGesture(Key.B, ModifierKeys.Control);
        });

        // ── Connection Command ──
        registry.Register("engine.connect", async () =>
        {
            if (hub.State == ConnectionState.Disconnected)
                await hub.ConnectAsync();
        }, cmd =>
        {
            cmd.Title = "连接引擎"; cmd.Category = "系统";
            cmd.DefaultGesture = new KeyGesture(Key.F5, ModifierKeys.Control);
        });

        // ── Help ──
        registry.Register("help.about", () =>
        {
            MessageBox.Show("TradingStudio Terminal v0.3.0\n\nWPF + SignalR + OxyPlot\n个人量化交易监控客户端",
                "About", MessageBoxButton.OK, MessageBoxImage.Information);
        }, cmd => { cmd.Title = "关于"; cmd.Category = "帮助"; });
    }

    private static void InitializePanels(IServiceProvider sp)
    {
        var pm = sp.GetRequiredService<PanelManager>();
        var dashboard = sp.GetRequiredService<DashboardViewModel>();

        pm.Register("dashboard",  "仪表盘", PanelLocation.Sidebar, () => dashboard, order: 0);
        pm.Register("chart",      "K线图",  PanelLocation.Sidebar,
            () => sp.GetRequiredService<ChartViewModel>(), order: 1);
        pm.Register("backtest",   "回测",   PanelLocation.Sidebar,
            () => sp.GetRequiredService<BacktestViewModel>(), order: 2);

        pm.Show("dashboard");
    }

    private static MainWindow GetMainWindow(IServiceProvider sp)
        => sp.GetRequiredService<MainWindow>();
}
