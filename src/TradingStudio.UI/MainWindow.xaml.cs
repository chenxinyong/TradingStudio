using System.Windows;
using Microsoft.AspNetCore.SignalR.Client;

namespace TradingStudio.UI;

/// <summary>
/// 主窗口 — Dashboard 总览面板。
/// Phase 3 实现。
/// </summary>
public partial class MainWindow : Window
{
    private HubConnection? _connection;

    public MainWindow()
    {
        InitializeComponent();
    }

    // Phase 3: 连接 SignalR Hub
    // private async Task ConnectAsync()
    // {
    //     _connection = new HubConnectionBuilder()
    //         .WithUrl("http://localhost:5199/hubs/engine")
    //         .WithAutomaticReconnect()
    //         .Build();
    //
    //     _connection.On<MonitorAlert>("Alert", OnAlert);
    //     _connection.On<OrderEvent>("OrderUpdate", OnOrderUpdate);
    //     _connection.On<HealthSnapshot>("HealthUpdate", OnHealthUpdate);
    //
    //     await _connection.StartAsync();
    //     await _connection.InvokeAsync("SubscribeStrategy", "ma-cross-rb");
    // }

    // Phase 3: 面板布局
    // ┌──────────┬───────────────┐
    // │ 策略列表  │   K线图表      │
    // │ ├─ ma-cross│               │
    // │ ├─ bollinger│              │
    // │ ├─ spread-rb│              │
    // │          │               │
    // │ 订单监控  │  告警中心      │
    // │          │               │
    // └──────────┴───────────────┘
}
