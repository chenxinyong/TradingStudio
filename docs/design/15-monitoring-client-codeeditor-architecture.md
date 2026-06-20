# 15 — 监控客户端架构设计（借鉴 CodeEditor MVVM 模式）

> 基于 CodeEditor 项目的 6 大架构模式，重新设计 TradingStudio WPF 监控客户端。
> 核心思路：命令系统统一操作入口、EventBus 解耦模块通信、面板系统管理多视图切换。
>
> **日期**: 2026-06-20 | **状态**: 设计中

---

## 目录

1. [架构总览](#1-架构总览)
2. [项目结构](#2-项目结构)
3. [Bootstrapper — DI 组装](#3-bootstrapper--di-组装)
4. [命令系统](#4-命令系统)
5. [EventBus — 事件总线](#5-eventbus--事件总线)
6. [Shell — 窗口布局](#6-shell--窗口布局)
7. [面板系统](#7-面板系统)
8. [数据接入层](#8-数据接入层)
9. [实施计划](#9-实施计划)

---

## 1. 架构总览

### 1.1 CodeEditor 模式映射

| CodeEditor 模式 | TradingStudio 监控客户端用途 |
|-----------------|---------------------------|
| `Bootstrapper.ConfigureServices()` | DI 组装：SignalR Client + ViewModels + Windows |
| `CommandRegistry` + `EditorCommand` | 统一操作入口：F5=买入 F6=卖出 Esc=全撤 |
| `EventBus` pub/sub | Tick → 策略信号 → UI 面板 解耦通信 |
| `ActivityBar + Sidebar` 面板切换 | 仪表盘/行情/策略/日志 4 面板切换 |
| `PanelManager` 懒加载面板 | 按需创建 K 线面板、策略详情面板 |
| `ShellViewModel` MVVM Hub | 主窗口 ViewModel，持有所有子面板引用 |
| `KeybindingRegistry` | 交易快捷键绑定 |
| `InputAdapter` | 平台无关键盘抽象（预留 Avalonia 迁移） |

### 1.2 三层通信

```
┌──────────────────────────────────────────────────────────────┐
│  TradingStudio.exe (localhost:5199)  — 引擎进程              │
│  ┌────────────────────┐  ┌────────────────────────────────┐  │
│  │ EngineHub (SignalR) │  │ EngineMonitorApi (REST)         │  │
│  │  → TickSnapshot     │  │  → GET /api/health              │  │
│  │  → PortfolioUpdated │  │  → GET /api/portfolio           │  │
│  │  → StrategiesUpdated│  │  → GET /api/strategies          │  │
│  │  → OrderUpdated     │  │  → POST /api/strategies/{id}/pause│
│  │  → AlertRaised      │  │  → POST /api/orders/close-position│
│  └────────────────────┘  └────────────────────────────────┘  │
└──────────────────────┬───────────────────────────────────────┘
                       │ WebSocket / HTTP
┌──────────────────────▼───────────────────────────────────────┐
│  TradingStudio.UI.exe  — WPF 监控客户端                      │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  EngineHubClient (SignalR → EventBus.Publish)           │  │
│  └──────────┬─────────────────────────────────────────────┘  │
│             │ EventBus                                       │
│  ┌──────────▼─────────────────────────────────────────────┐  │
│  │  ViewModels (订阅 EventBus，更新 ObservableCollection)  │  │
│  │  ├─ DashboardVM  ← TickReceived, PortfolioChanged...   │  │
│  │  ├─ ChartVM      ← TickReceived, BarLoaded...           │  │
│  │  ├─ StrategyVM   ← StrategyStateChanged...              │  │
│  │  └─ OrderVM      ← OrderUpdated...                     │  │
│  └──────────┬─────────────────────────────────────────────┘  │
│             │ Data Binding                                   │
│  ┌──────────▼─────────────────────────────────────────────┐  │
│  │  Views (XAML) — OxyPlot 图表 + DataGrid + Indicator    │  │
│  └────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────┘
```

---

## 2. 项目结构

```
src/TradingStudio.UI/
├── TradingStudio.UI.csproj          ← WPF + net10.0-windows
│
├── App.xaml / App.xaml.cs           ← 入口：启动托盘 + Bootstrapper
│
├── Startup/
│   └── Bootstrapper.cs              ← DI 组装（借鉴 CodeEditor）
│
├── Core/                            ← 平台无关抽象（借鉴 Editor.Core）
│   ├── Commands/
│   │   ├── TradingCommand.cs        ← 交易命令（如 EditorCommand）
│   │   ├── CommandRegistry.cs       ← 命令注册/查找/执行
│   │   └── CommandPalette.cs        ← Ctrl+Shift+P 命令面板
│   ├── Input/
│   │   ├── Key.cs                   ← 平台无关按键枚举
│   │   ├── KeyGesture.cs            ← 按键+修饰键
│   │   └── ModifierKeys.cs          ← 修饰键 flags
│   ├── Messaging/
│   │   └── EventBus.cs              ← pub/sub 事件总线
│   ├── Configuration/
│   │   └── ClientConfig.cs          ← 客户端配置（引擎地址、快捷键）
│   └── Models/
│       ├── DashboardSnapshot.cs     ← 仪表盘数据模型
│       ├── AlertRecord.cs           ← 告警记录
│       └── StrategySnapshot.cs      ← 策略状态快照
│
├── Services/                        ← 数据接入层
│   ├── EngineHubClient.cs           ← SignalR 连接 + 事件路由到 EventBus
│   ├── EngineRestClient.cs          ← REST API 降级路径
│   ├── BarHistoryLoader.cs          ← SQLite 直读历史 K 线
│   └── AlertService.cs              ← Windows 桌面通知
│
├── ViewModels/
│   ├── ShellViewModel.cs            ← 主窗口 VM（持有所有子面板引用）
│   ├── DashboardViewModel.cs        ← 4 象限仪表盘数据
│   ├── ChartViewModel.cs            ← K 线图 VM
│   ├── StrategyListViewModel.cs     ← 策略列表
│   ├── StrategyDetailViewModel.cs   ← 策略详情
│   ├── OrderHistoryViewModel.cs     ← 订单/成交历史
│   ├── AlertListViewModel.cs        ← 告警列表
│   └── StatusBarViewModel.cs        ← 状态栏
│
├── Panels/
│   ├── ActivityBarViewModel.cs      ← 左侧图标栏（借鉴 CodeEditor）
│   ├── PanelManager.cs              ← 面板注册/懒加载/切换
│   ├── PanelDescriptor.cs           ← 面板元数据
│   └── PanelLocation.cs             ← 面板位置枚举
│
├── Commands/
│   └── InputAdapter.cs              ← Core Key ↔ WPF Key 双向映射
│
├── Converters/
│   ├── BoolToVisibilityConverter.cs
│   ├── SeverityToColorConverter.cs  ← Warning=黄 Error=红 Info=蓝
│   ├── PnLToBrushConverter.cs       ← 盈亏→颜色
│   └── ConnectionStateConverter.cs  ← 连接状态→指示灯
│
├── Views/
│   ├── MainWindow.xaml/.cs          ← Shell 布局
│   ├── DashboardView.xaml           ← 仪表盘 4 象限
│   ├── ChartView.xaml               ← K 线图（OxyPlot）
│   ├── StrategyListView.xaml        ← 策略列表
│   ├── StrategyDetailView.xaml      ← 策略详情
│   ├── OrderHistoryView.xaml        ← 订单历史
│   └── AlertListView.xaml           ← 告警列表
│
└── Themes/
    ├── DarkTheme.xaml               ← 深色主题（交易员偏好）
    └── LightTheme.xaml              ← 浅色主题
```

---

## 3. Bootstrapper — DI 组装

借鉴 CodeEditor 的 `Bootstrapper.ConfigureServices()` 模式，所有服务在启动时注册。

```csharp
// Bootstrapper.cs
public static class Bootstrapper
{
    public static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // ── Core 层 ──
        services.AddSingleton<EventBus>();
        services.AddSingleton<ClientConfig>();
        services.AddSingleton<CommandRegistry>();
        services.AddSingleton<KeybindingRegistry>();

        // ── 数据接入 ──
        services.AddSingleton<EngineHubClient>();       // SignalR
        services.AddSingleton<EngineRestClient>();      // REST 降级
        services.AddSingleton<BarHistoryLoader>();      // 历史 K 线
        services.AddSingleton<AlertService>();          // 桌面通知

        // ── ViewModels ──
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddTransient<ChartViewModel>();         // 每个图表窗口独立实例
        services.AddSingleton<StrategyListViewModel>();
        services.AddTransient<StrategyDetailViewModel>();
        services.AddSingleton<OrderHistoryViewModel>();
        services.AddSingleton<AlertListViewModel>();
        services.AddSingleton<StatusBarViewModel>();

        // ── 面板系统 ──
        services.AddSingleton<ActivityBarViewModel>();
        services.AddSingleton<PanelManager>();

        // ── UI Windows ──
        services.AddSingleton<MainWindow>();
        services.AddTransient<ChartWindow>();

        var provider = services.BuildServiceProvider();

        // ── 后构建初始化 ──
        InitializeCommands(provider);
        InitializePanels(provider);
        InitializeKeybindings(provider);
        ConnectToEngine(provider);      // 启动 SignalR 连接

        return provider;
    }

    private static void InitializeCommands(IServiceProvider sp)
    {
        var registry = sp.GetRequiredService<CommandRegistry>();

        // ── 交易命令 ──
        registry.Register("trade.buy", async () =>
        {
            // 买入当前选中品种
        }, cmd =>
        {
            cmd.Title = "买入"; cmd.Category = "交易";
            cmd.DefaultGesture = new KeyGesture(Key.F5, ModifierKeys.None);
        });

        registry.Register("trade.sell", async () => { /* ... */ }, cmd =>
        {
            cmd.Title = "卖出"; cmd.Category = "交易";
            cmd.DefaultGesture = new KeyGesture(Key.F6, ModifierKeys.None);
        });

        registry.Register("trade.cancelAll", async () => { /* ... */ }, cmd =>
        {
            cmd.Title = "全撤"; cmd.Category = "交易";
            cmd.DefaultGesture = new KeyGesture(Key.Escape, ModifierKeys.None);
        });

        registry.Register("trade.closePosition", async () => { /* ... */ }, cmd =>
        {
            cmd.Title = "平仓"; cmd.Category = "交易";
            cmd.DefaultGesture = new KeyGesture(Key.F7, ModifierKeys.None);
        });

        // ── 视图命令 ──
        registry.Register("view.dashboard", () =>
        {
            var vm = GetShellViewModel(sp);
            vm.ActivityBar.SelectedItem = vm.ActivityBar.Items[0]; // Dashboard
        }, cmd =>
        {
            cmd.Title = "仪表盘"; cmd.Category = "视图";
            cmd.DefaultGesture = new KeyGesture(Key.D, ModifierKeys.Control);
        });

        registry.Register("view.chart", () =>
        {
            var vm = GetShellViewModel(sp);
            vm.OpenChartWindow();
        }, cmd =>
        {
            cmd.Title = "K线图"; cmd.Category = "视图";
            cmd.DefaultGesture = new KeyGesture(Key.C, ModifierKeys.Control);
        });

        registry.Register("view.strategies", () =>
        {
            var vm = GetShellViewModel(sp);
            vm.ActivityBar.SelectedItem = vm.ActivityBar.Items.First(i => i.Id == "strategies");
        }, cmd =>
        {
            cmd.Title = "策略列表"; cmd.Category = "视图";
            cmd.DefaultGesture = new KeyGesture(Key.S, ModifierKeys.Control);
        });

        registry.Register("view.orders", () =>
        {
            var vm = GetShellViewModel(sp);
            vm.ActivityBar.SelectedItem = vm.ActivityBar.Items.First(i => i.Id == "orders");
        }, cmd =>
        {
            cmd.Title = "订单历史"; cmd.Category = "视图";
            cmd.DefaultGesture = new KeyGesture(Key.O, ModifierKeys.Control);
        });

        registry.Register("view.commandPalette", () =>
        {
            var vm = GetShellViewModel(sp);
            vm.ShowCommandPalette();
        }, cmd =>
        {
            cmd.Title = "命令面板"; cmd.Category = "视图";
            cmd.DefaultGesture = new KeyGesture(Key.P, ModifierKeys.Control | ModifierKeys.Shift);
        });

        // ── 系统命令 ──
        registry.Register("system.connect", async () => { /* SignalR 重连 */ }, cmd =>
        {
            cmd.Title = "连接引擎"; cmd.Category = "系统";
        });

        registry.Register("system.settings", () => { /* 打开设置 */ }, cmd =>
        {
            cmd.Title = "设置"; cmd.Category = "系统";
        });
    }

    private static void InitializePanels(IServiceProvider sp)
    {
        var pm = sp.GetRequiredService<PanelManager>();
        var dashboard = sp.GetRequiredService<DashboardViewModel>();

        pm.Register("dashboard",  "仪表盘",  PanelLocation.Sidebar, () => dashboard, order: 0);
        pm.Register("strategies", "策略",    PanelLocation.Sidebar, () => sp.GetRequiredService<StrategyListViewModel>(), order: 1);
        pm.Register("orders",     "订单",    PanelLocation.Sidebar, () => sp.GetRequiredService<OrderHistoryViewModel>(), order: 2);
        pm.Register("alerts",     "告警",    PanelLocation.Bottom,  () => sp.GetRequiredService<AlertListViewModel>(), order: 0);
        pm.Register("logs",       "日志",    PanelLocation.Bottom,  () => sp.GetRequiredService<StatusBarViewModel>(), order: 1);

        pm.Show("dashboard"); // 默认显示仪表盘
    }
}
```

---

## 4. 命令系统

### 4.1 TradingCommand（借鉴 EditorCommand）

```csharp
// Core/Commands/TradingCommand.cs
public class TradingCommand
{
    public string Id { get; }                    // "trade.buy"
    public string Title { get; set; }            // "买入"
    public string Category { get; set; }         // "交易"
    public KeyGesture? DefaultGesture { get; set; } // F5
    public Func<object?, Task> Handler { get; }  // 执行体
    public Func<object?, bool>? When { get; init; } // 风控门禁

    public string DisplayLabel =>
        string.IsNullOrEmpty(Category) ? Title : $"{Category}: {Title}";

    public bool CanExecute(object? param = null) =>
        When?.Invoke(param) ?? true;

    public async Task ExecuteAsync(object? param = null)
    {
        if (CanExecute(param))
            await Handler(param);
    }
}
```

### 4.2 风控门禁（When 谓词）

CodeEditor 的 `When` 谓词从未被使用。TradingStudio 利用它做风控前置检查：

```csharp
// "trade.buy" 只能在以下条件都可执行
registry.Register("trade.buy", async () => { /* 发送买单 */ }, cmd =>
{
    cmd.Title = "买入"; cmd.Category = "交易";
    cmd.DefaultGesture = new KeyGesture(Key.F5);

    // 🆕 风控门禁
    cmd.When = _ =>
    {
        var engine = _engineHubClient;
        return engine.IsConnected
            && engine.CurrentSession?.IsInSession == true
            && engine.Portfolio?.Cash > 0;   // 有可用资金
    };
});
```

当引擎断开或不在交易时段，`Ctrl+F5` 和菜单"买入"按钮自动禁用。

### 4.3 命令的三种触发路径（与 CodeEditor 一致）

```
F5 快捷键 ──→ WPF InputBinding ──→ InputAdapter ──→ KeybindingRegistry
                                                          │
菜单 买入 ──→ MenuItem.Click ──→ Menu_BuyClick ─────────┤
                                                          ▼
命令面板 ──→ Ctrl+Shift+P "买入" ──→ CommandPalette ──→ CommandRegistry.ExecuteAsync("trade.buy")
                                                          │
                                                          ▼
                                                    TradingCommand.Handler()
                                                          │
                                                          ▼
                                                    发送买单到 CTP
```

---

## 5. EventBus — 事件总线

### 5.1 消息类型

```csharp
// 来自 SignalR → EventBus 的事件
public record TickReceived(string InstrumentId, decimal LastPrice, int Volume, DateTime Time);
public record PortfolioChanged(decimal Equity, decimal Cash, decimal MarginUsed, decimal TotalPnL);
public record StrategyStateChanged(string StrategyId, string State, decimal PnL);
public record OrderUpdated(string OrderId, string Status, string InstrumentId, decimal Price, int Volume);
public record AlertRaised(string Message, AlertSeverity Severity, DateTime Time);
public record ConnectionStateChanged(bool IsConnected, string? Error);
public record SessionChanged(string SessionName, bool IsInSession);

public enum AlertSeverity { Info, Warning, Error, Critical }
```

### 5.2 ViewModel 订阅

```csharp
// DashboardViewModel.cs
public class DashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly List<IDisposable> _subscriptions = new();

    public DashboardViewModel(EventBus eventBus)
    {
        _subscriptions.Add(eventBus.Subscribe<TickReceived>(OnTickReceived));
        _subscriptions.Add(eventBus.Subscribe<PortfolioChanged>(OnPortfolioChanged));
        _subscriptions.Add(eventBus.Subscribe<AlertRaised>(OnAlertRaised));
        _subscriptions.Add(eventBus.Subscribe<ConnectionStateChanged>(OnConnectionChanged));
    }

    private void OnTickReceived(TickReceived tick)
    {
        // 更新仪表盘行情计数
        Application.Current.Dispatcher.Invoke(() =>
        {
            TickCount = _tickCount + 1;
            OnPropertyChanged(nameof(TickCount));
        });
    }

    public void Dispose()
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
    }
}
```

### 5.3 SignalR → EventBus 桥接

```csharp
// Services/EngineHubClient.cs
public class EngineHubClient
{
    private HubConnection? _connection;
    private readonly EventBus _eventBus;

    public EngineHubClient(EventBus eventBus, ClientConfig config)
    {
        _eventBus = eventBus;
        _connection = new HubConnectionBuilder()
            .WithUrl($"{config.EngineUrl}/hubs/engine")
            .WithAutomaticReconnect()
            .Build();

        // SignalR 服务器推送 → EventBus 事件
        _connection.On<TickSnapshot>("TickSnapshot", snapshot =>
            _eventBus.Publish(new TickReceived(snapshot.InstrumentId, ...)));

        _connection.On<Portfolio>("PortfolioUpdated", portfolio =>
            _eventBus.Publish(new PortfolioChanged(portfolio.Equity, ...)));

        _connection.On<StrategySnapshot[]>("StrategiesUpdated", snapshots =>
        {
            foreach (var s in snapshots)
                _eventBus.Publish(new StrategyStateChanged(s.Id, s.State, s.PnL));
        });

        _connection.On<Order>("OrderUpdated", order =>
            _eventBus.Publish(new OrderUpdated(order.Id, order.Status, ...)));

        _connection.On<Alert>("Alert", alert =>
            _eventBus.Publish(new AlertRaised(alert.Message, alert.Severity, alert.Time)));

        _connection.StateChanged += state =>
            _eventBus.Publish(new ConnectionStateChanged(
                state == HubConnectionState.Connected, null));
    }

    public async Task ConnectAsync() => await _connection!.StartAsync();
}
```

---

## 6. Shell — 窗口布局

### 6.1 布局树（借鉴 CodeEditor MainWindow.xaml 的 DockPanel 结构）

```
DockPanel (LastChildFill=True)
├── MenuBar Grid (Top, 28px)                            ← 菜单 + 命令面板搜索
│   ├── Menu (Left): 交易 | 视图 | 策略 | 帮助
│   └── CommandPaletteInput (Right, 250px)
│
├── StatusBar (Bottom, 28px)                             ← 连接状态·权益·时段·时间
│   ├── 🟢 已连接 | 日盘 | 距收盘 02:15:32
│   ├── 💰 权益: 102,350 | 可用: 85,200 | 保证金: 17.1%
│   └── Ln 1, Col 1 (当前选中品种/合约)
│
├── ActivityBar Grid (Left, 48px)                        ← 借鉴 CodeEditor 图标栏
│   ├── ListBox (Top):    📊仪表盘  📈K线  📋策略  📜订单
│   └── ListBox (Bottom): ⚙设置
│
├── Sidebar Grid (Left, 300px)                           ← 活动面板区域
│   └── Content Grid (4 面板，按 Visibility 切换)
│       ├── DashboardContent  (4 象限仪表盘)
│       ├── StrategyContent   (策略列表)
│       ├── OrderContent      (订单历史)
│       └── AlertContent      (告警列表)
│
└── Main Content Grid (剩余空间)                          ← 主视图
    └── ContentControl (ActiveView)                      ← 根据 ActivityBar 切换
        ├── ChartView (OxyPlot K 线图)
        └── DetailView (策略详情)
```

### 6.2 ShellViewModel（借鉴 CodeEditor）

```csharp
// ViewModels/ShellViewModel.cs
public class ShellViewModel : INotifyPropertyChanged
{
    public ShellViewModel(
        EventBus eventBus,
        CommandRegistry commandRegistry,
        ClientConfig config,
        ActivityBarViewModel activityBar,
        DashboardViewModel dashboard,
        ChartViewModel chart,
        StrategyListViewModel strategies,
        OrderHistoryViewModel orders,
        AlertListViewModel alerts,
        StatusBarViewModel statusBar)
    {
        EventBus = eventBus;
        CommandRegistry = commandRegistry;
        Config = config;
        ActivityBar = activityBar;
        Dashboard = dashboard;
        Chart = chart;
        Strategies = strategies;
        Orders = orders;
        Alerts = alerts;
        StatusBar = statusBar;

        // 连接状态 → 状态栏
        eventBus.Subscribe<ConnectionStateChanged>(change =>
        {
            StatusBar.ConnectionState = change.IsConnected
                ? ConnectionState.Connected : ConnectionState.Disconnected;
        });
    }

    public ActivityBarViewModel ActivityBar { get; }
    public DashboardViewModel Dashboard { get; }
    public ChartViewModel Chart { get; }
    public StrategyListViewModel Strategies { get; }
    public OrderHistoryViewModel Orders { get; }
    public AlertListViewModel Alerts { get; }
    public StatusBarViewModel StatusBar { get; }

    // ── 命令面板 ──
    private bool _isCommandPaletteOpen;
    private string _commandPaletteQuery = "";

    public bool IsCommandPaletteOpen { get; set; }
    public string CommandPaletteQuery { get; set; }
    public IEnumerable<TradingCommand> CommandPaletteResults =>
        _commandRegistry.Search(_commandPaletteQuery);

    public void ShowCommandPalette() => IsCommandPaletteOpen = true;
}
```

### 6.3 ActivityBar（交易面板图标）

```csharp
// Panels/ActivityBarViewModel.cs
public class ActivityBarViewModel
{
    public ObservableCollection<ActivityBarItem> Items { get; } = new()
    {
        new("dashboard",  "📊", "仪表盘",  0),
        new("chart",      "📈", "K线图",   1),
        new("strategies", "📋", "策略",    2),
        new("orders",     "📜", "订单",    3),
        new("alerts",     "🔔", "告警",    4),
    };

    public ObservableCollection<ActivityBarItem> BottomItems { get; } = new()
    {
        new("settings", "⚙", "设置", 99),
    };
}
```

---

## 7. 面板系统

### 7.1 仪表盘（Dashboard）— 4 象限布局

```
┌───────────────────────┬───────────────────────┐
│  系统健康              │  风控概览               │
│  ┌─────────────────┐  │  ┌─────────────────┐  │
│  │ 🟢 已连接        │  │  │ 总权益  ¥102,350│  │
│  │ 日盘 距收盘2h15m │  │  │ 可用    ¥85,200 │  │
│  │ 品种: 42         │  │  │ 保证金  17.1%   │  │
│  │ 运行: 3h42m      │  │  │ 持仓: 3 品种     │  │
│  └─────────────────┘  │  └─────────────────┘  │
├───────────────────────┴───────────────────────┤
│  活跃告警（最近 20 条）                         │
│  ┌──────────────────────────────────────────┐  │
│  │ 🔴 14:32  ag2608 保证金不足 (30,200)      │  │
│  │ 🟡 14:28  rb2610 接近涨停                 │  │
│  │ 🔵 14:15  夜盘连接已恢复                   │  │
│  └──────────────────────────────────────────┘  │
├───────────────────────┬───────────────────────┤
│  策略状态              │  最近成交               │
│  ┌─────────────────┐  │  ┌─────────────────┐  │
│  │ ✅ SMA_Cross     │  │  │ 14:30 B rb2610  │  │
│  │    PnL +1,200    │  │  │   ¥3,520 ×5手   │  │
│  │ ✅ ChanLun       │  │  │ 14:25 S ag2608  │  │
│  │    PnL +3,450    │  │  │   ¥5,820 ×2手   │  │
│  │ ⏸️ MeanRev (暂停)│  │  │ 14:20 B rb2610  │  │
│  └─────────────────┘  │  └─────────────────┘  │
└───────────────────────┴───────────────────────┘
```

### 7.2 K 线图（Chart）

```
┌─────────────────────────────────────────────┐
│ 品种选择: [rb2610 ▼]  周期: [1min ▼]        │
│ 指标: ☑ MA(20) ☑ MA(60) ☐ BOLL ☑ MACD    │
├─────────────────────────────────────────────┤
│                                             │
│  ┌─ K 线 + MA ─────────────────────────┐   │
│  │  ██████   ██                         │   │
│  │  ██  ██ ████ ██                      │   │
│  │  ██  ██ ██ ██ ██                     │   │
│  └──────────────────────────────────────┘   │
│  ┌─ 成交量 ─────────────────────────────┐   │
│  │  ▓▓▓▓    ▓▓▓▓   ▓▓                    │   │
│  └──────────────────────────────────────┘   │
│  ┌─ MACD ───────────────────────────────┐   │
│  │  ─── 柱状图 + 信号线 ───              │   │
│  └──────────────────────────────────────┘   │
│                                             │
└─────────────────────────────────────────────┘
```

**实现要点**：
- OxyPlot 4 窗格（K线 + 成交量 + MACD + RSI）共享 X 轴
- 历史 Bar 从 SQLite 加载（`BarHistoryLoader`）
- 实时 Tick 走 SignalR，累积为 Bar 后追加到图表
- 买卖点标记（三角形 ▲▼ 叠加在 K 线上）

---

## 8. 数据接入层

### 8.1 数据流（借鉴 CodeEditor 的 LSP 三级降级模式）

```
优先路径:   SignalR WebSocket 实时推送
            → EngineHubClient → EventBus → ViewModels

降级路径1:  REST API 轮询 (SignalR 断开时)
            → EngineRestClient.GetAsync("/api/health") → EventBus → ViewModels

降级路径2:  health.json 文件直读 (引擎进程也挂了)
            → FileWatcher + JSON 反序列化 → EventBus → ViewModels

离线路径:   SQLite 直读历史 Bar
            → BarHistoryLoader  → OxyPlot 渲染
```

### 8.2 EngineHubClient（核心服务）

```csharp
public class EngineHubClient : IDisposable
{
    private HubConnection? _connection;
    private readonly EventBus _eventBus;
    private readonly ClientConfig _config;
    private readonly ILogger<EngineHubClient> _log;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync()
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{_config.EngineUrl}/hubs/engine")
            .WithAutomaticReconnect(new RetryPolicy())
            .Build();

        RegisterHandlers();

        _connection.Closed += async (error) =>
        {
            _eventBus.Publish(new ConnectionStateChanged(false, error?.Message));
            await Task.Delay(3000);
            await ConnectAsync();  // 重连
        };

        await _connection.StartAsync();
        _eventBus.Publish(new ConnectionStateChanged(true, null));
    }

    private void RegisterHandlers()
    {
        _connection!.On<TickSnapshot>("TickSnapshot", data =>
            _eventBus.Publish(new TickReceived(data.InstrumentId, data.LastPrice, ...)));

        _connection.On<Portfolio>("PortfolioUpdated", data =>
            _eventBus.Publish(new PortfolioChanged(data.Equity, data.Cash, ...)));

        // ... 其余 Handler 映射
    }
}
```

---

## 9. 实施计划

### 总览

| 阶段 | 内容 | 时间 | 借鉴 CodeEditor 的 |
|------|------|------|-------------------|
| Phase 1 | 地基 — 项目骨架 + DI + Core 层 | 3h | Bootstrapper, EventBus, Command, Input |
| Phase 2 | Shell + 面板系统 | 4h | ShellViewModel, ActivityBar, PanelManager, MainWindow.xaml |
| Phase 3 | 数据接入 — SignalR + ViewModels 集成 | 4h | EngineHubClient → EventBus → ViewModels |
| Phase 4 | K 线图 | 3h | OxyPlot 集成, BarHistoryLoader |
| Phase 5 | 命令系统完善 + 快捷键 | 2h | CommandRegistry, KeybindingRegistry |
| Phase 6 | 系统托盘 + 告警 | 2h | AlertService, NotifyIcon |
| Phase 7 | 降级模式 + 错误处理 | 1.5h | REST 降级, health.json 直读 |
| Phase 8 | 测试 + 调优 | 1.5h | |
| **总计** | | **21h** | |

### Phase 1: 地基 (3h)

| # | 任务 | 时间 | 参考 |
|---|------|------|------|
| 1.1 | 创建 `TradingStudio.UI.csproj` (WPF + net10.0) | 20min | Editor.App.csproj |
| 1.2 | 实现 `Core/` 下类型：TradingCommand, CommandRegistry, KeybindingRegistry, EventBus, Key/KeyGesture/ModifierKeys | 1h | 直接复制 Editor.Core 对应文件，改命名空间 |
| 1.3 | 实现 `Core/Models/` 数据模型 | 30min | DashboardSnapshot, AlertRecord |
| 1.4 | 实现 `App.xaml.cs` + `Bootstrapper.ConfigureServices()` | 40min | Editor.App 对应文件 |
| 1.5 | 实现 `ClientConfig`（引擎地址、快捷键） | 20min | EditorConfig 精简版 |

**验证**: `dotnet build` 通过，Bootstrapper 成功构建 `IServiceProvider`

### Phase 2: Shell + 面板系统 (4h)

| # | 任务 | 时间 | 参考 |
|---|------|------|------|
| 2.1 | `MainWindow.xaml` DockPanel 布局 | 1.5h | CodeEditor MainWindow.xaml 结构 |
| 2.2 | `ShellViewModel` + 子面板 VM 骨架 | 1h | CodeEditor ShellViewModel |
| 2.3 | `ActivityBarViewModel` + XAML 图标 | 45min | CodeEditor 对应文件 |
| 2.4 | `PanelManager` + 面板注册 | 30min | CodeEditor PanelManager |
| 2.5 | DarkTheme.xaml 交易主题 | 15min | CodeEditor DarkTheme 配色调整 |

**验证**: 窗口启动，ActivityBar 图标可点击切换面板

### Phase 3: 数据接入 (4h)

| # | 任务 | 时间 | 参考 |
|---|------|------|------|
| 3.1 | `EngineHubClient` SignalR 连接 + Handler 注册 | 1.5h | CodeEditor LspClient 的 connect/dispatch 模式 |
| 3.2 | `EngineRestClient` HTTP 降级路径 | 45min | |
| 3.3 | `DashboardViewModel` 完整实现（订阅 EventBus） | 1h | |
| 3.4 | `StrategyListViewModel` + `OrderHistoryViewModel` | 45min | |

**验证**: 引擎运行 + 客户端连接 → Dashboard 实时更新

### Phase 4: K 线图 (3h)

| # | 任务 | 时间 | 参考 |
|---|------|------|------|
| 4.1 | `ChartViewModel` OxyPlot 模型创建 | 1h | |
| 4.2 | `ChartView.xaml` 4 窗格布局 (K线/量/MACD/RSI) | 1h | |
| 4.3 | `BarHistoryLoader` SQLite 直读 | 30min | |
| 4.4 | 实时 Tick → Bar 追加 + 自动滚动 | 30min | TextEditorHost TextChanged 模式 |

**验证**: 选择品种 → 加载历史 K 线 → 实时 Bar 滚动

### Phase 5: 命令系统 (2h)

| # | 任务 | 时间 | 参考 |
|---|------|------|------|
| 5.1 | 注册 12 个交易/视图命令 + When 风控门禁 | 1h | Bootstrapper.InitializeCommands |
| 5.2 | `InputAdapter` WPF KeyBinding 映射 | 30min | CodeEditor InputAdapter |
| 5.3 | 命令面板 `Ctrl+Shift+P` Popup | 30min | CodeEditor CommandPalette |

**验证**: F5=买入, Ctrl+D=仪表盘, Ctrl+Shift+P=命令面板搜索"平仓"

### Phase 6: 系统托盘 + 告警 (2h)

| # | 任务 | 时间 |
|---|------|------|
| 6.1 | NotifyIcon + 右键菜单 | 45min |
| 6.2 | `AlertService` Windows 桌面通知 | 45min |
| 6.3 | 连接状态指示灯 (绿/黄/红) | 30min |

**验证**: 托盘图标显示，告警弹出桌面通知

### Phase 7: 降级模式 (1.5h)

| # | 任务 | 时间 |
|---|------|------|
| 7.1 | health.json 直读降级 | 40min |
| 7.2 | REST 轮询降级（SignalR 断开时） | 30min |
| 7.3 | 异常兜底 UI（"引擎不可用"提示） | 20min |

**验证**: 引擎进程 Kill → 客户端自动降级 → 重启引擎 → 自动恢复

### Phase 8: 测试 + 调优 (1.5h)

| # | 任务 | 时间 |
|---|------|------|
| 8.1 | 端到端流程：启动引擎 → 客户端 → 收行情 → 看图表 | 1h |
| 8.2 | 内存/CPU 监控（长时间运行） | 30min |

---

## 10. 第一版明确不做

| 功能 | 原因 |
|------|------|
| StrategyDetail 的实时参数调整 | 需要 Phase 3 服务端支持 |
| 策略脚本编辑器 | 独立项目（6.5 天，见上轮讨论） |
| LiveCharts2 / HandyControl | Phase 4（OxyPlot 够用） |
| 移动端推送 | Phase 5 |
| 多窗口布局保存/恢复 | Phase 3 |
| BacktestAnalysis 窗口 | 先手动跑 ToolBox，再做 UI |

---

## 11. CodeEditor 模式复用的量化

| CodeEditor 文件 | 复用方式 | 改动量 |
|-----------------|---------|--------|
| `Editor.Core/Commands/EditorCommand.cs` | 复制 → 改名为 `TradingCommand` | <5% |
| `Editor.Core/Commands/CommandRegistry.cs` | 直接复制 | <5% |
| `Editor.Core/Commands/KeybindingRegistry.cs` | 直接复制 | <5% |
| `Editor.Core/Messaging/EventBus.cs` | 直接复制 | 0% |
| `Editor.Core/Input/Key.cs` | 直接复制 | 0% |
| `Editor.Core/Input/KeyGesture.cs` | 直接复制 | 0% |
| `Editor.Core/Input/ModifierKeys.cs` | 直接复制 | 0% |
| `Editor.UI/Commands/InputAdapter.cs` | 直接复制 | <10% |
| `Editor.UI/Panels/PanelManager.cs` | 直接复制 | <10% |
| `Editor.UI/Panels/ActivityBarViewModel.cs` | 复制 → 改图标 | 20% |
| `Editor.UI/Shell/ShellViewModel.cs` | 复制 → 重写面板引用 | 50% |
| `Editor.App/Startup/Bootstrapper.cs` | 复制 → 改服务注册 | 70% |
| `Editor.UI/Shell/MainWindow.xaml` | 复制 → 改布局内容 | 60% |
| `Editor.UI/Themes/DarkTheme.xaml` | 复制 → 微调配色 | 10% |
| `Editor.UI/Converters/BoolToVisibilityConverter.cs` | 直接复制 | 0% |

**总代码量估算**：~800 行纯复制 + ~600 行新写/改写 = ~1400 行 C# + 500 行 XAML。

---

## 关联文档

- [14 — WPF 监控客户端设计 (v1.0)](14-wpf-monitoring-client-design.md) — 功能列表原始规格
- [13 — UI 技术选型](13-ui-technology-selection.md)
- [Phase 2 回测系统设计](phase2-backtest-design-v2.md)
