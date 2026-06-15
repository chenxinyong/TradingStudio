# TradingStudio WPF 量化终端 — 最终设计方案

> 参考:
> - [13 — UI 技术选型](./13-ui-technology-selection.md) — WPF + OxyPlot 决策依据
> - [14 — WPF 监控客户端设计](./14-wpf-monitoring-client-design.md) — Dashboard-first 简化方案 (2026-06-15)
> - AvalonDock 工作台布局 + Command Palette + EventAggregator + Workspace
>
> **注**: 2026-06-15 第一性原理验证后，设计方案从 "VS Code × Bloomberg Terminal" 简化为 Dashboard-first（14 号文档）。本文档保留完整工作台愿景，作为 Phase 4 参考。
>
> 目标体验: **VS Code × Bloomberg Terminal × TradingView Desktop**

---

## 目录

1. [整体架构](#1-整体架构)
2. [Shell 主界面](#2-shell-主界面)
3. [Docking 抽象层](#3-docking-抽象层)
4. [布局与 Workspace](#4-布局与-workspace)
5. [Command Palette](#5-command-palette-ctrlf1p)
6. [EventAggregator 消息总线](#6-eventaggregator-消息总线)
7. [Dashboardsfmfh总览](#7-总览-dashboard)
8. [策略监控](#8-策略监控)
9. [回测分析](#9-回测分析)
10. [行情图表](#10-行情图表)
11. [风控与告警](#11-风控与告警)
12. [订单流](#12-订单流)
13. [日志与终端](#13-日志与终端)
14. [策略编辑器](#14-策略编辑器)
15. [插件系统](#15-插件系统)
16. [MVVM 代码结构](#16-mvvm-代码结构)
17. [分阶段实施](#17-分阶段实施)
18. [技术栈决策](#18-技术栈决策)

---

## 1. 整体架构

```
TradingStudio.UI.exe (WPF)              TradingStudio.exe (localhost:5199)
        │                                        │
        │  SignalR Client                         │  SignalR Hub
        │  REST API                               │  REST API
        │                                         │
        ▼                                         ▼
   WPF 量化终端                             实盘引擎 (7×24)
  ┌─────────────────────────┐            ┌──────────────────────────┐
  │ AvalonDock 工作台        │◄──────────│ TradingEngine            │
  │  ├─ Tool Windows (固定)  │ SignalR   │  PortfolioManager        │
  │  ├─ Documents (工作区)   │ 实时推送  │  StrategyContainer       │
  │  └─ Bottom Panels (底部) │           │  ExecutionHandler        │
  │                          │           │  FeedbackMonitor         │
  │  EventAggregator         │           └──────────────────────────┘
  │  Command Palette         │
  │  Workspace Manager       │           回测 (客户端本地)
  │  Plugin Loader           │           ┌──────────────────────────┐
  └─────────────────────────┘            │ TradingEngine (内嵌)     │
                                         │  HistoricalBarFeed       │
                                         │  EngineReport            │
                                         └──────────────────────────┘
```

**核心原则**:
- UI 是临时访客，引擎是主人 — 断开不影响引擎运行
- ViewModel 不互相引用 — 通过 EventAggregator 消息通信
- 一个品种一个 Chart Document，一个策略一个 Monitor Document
- 回测在客户端本地执行，不依赖服务器

---

## 2. Shell 主界面

```
┌─ MenuBar ───────────────────────────────────────────────────────────┐
│ 文件  编辑  视图  运行  工具  帮助                                    │
├─ Command Bar ───────────────────────────────────────────────────────┤
│ [🔍 输入品种代码 >]   [▶ 运行回测] [📄 新策略] [🔌 连接]            │
│                                            |  ● SimNow 模拟盘       │
├──────────────────────┬───────────────────────────┬──────────────────┤
│                      │                           │                  │
│  左侧 Tool Windows   │   中央 Document 工作区     │ 右侧 Tool Windows│
│  (20%)               │   (60%)                   │  (20%)           │
│                      │                           │                  │
│  ┌────────────────┐  │  ┌─[Dashboard]──────┐    │  ┌────────────┐  │
│  │ EXPLORER       │  │  │ ● 策略  1  运行  │    │  │ WATCHLIST  │  │
│  │  ├─ 策略树     │  │  │ ● 权益 1,037K   │    │  │ rb2608 3584│  │
│  │  ├─ 账户       │  │  │ ● 今日 +4,200   │    │  │ ag2608 6520│  │
│  │  └─ 服务器     │  │  │                  │    │  │ i2609  798  │  │
│  └────────────────┘  │  │ ┌─权益曲线────┐  │    │  └────────────┘  │
│                      │  │ │ ▁▂▃▅▆▇ +15% │  │    │                  │
│  ┌────────────────┐  │  │ └─────────────┘  │    │  ┌────────────┐  │
│  │ STRATEGY       │  │  │                  │    │  │ POSITIONS  │  │
│  │  ├─ MaCross ●  │  │  │ ┌─策略状态表──┐  │    │  │ rb2608 +2  │  │
│  │  ├─ BollBreak ⏸│  │  │ │名称 状态 PnL │  │    │  │ ag2608 -1  │  │
│  │  └─ Spread ○   │  │  │ └─────────────┘  │    │  └────────────┘  │
│  └────────────────┘  │  │                  │    │                  │
│                      │  └─[策略监控]──────┘    │  ┌────────────┐  │
│  ┌────────────────┐  │  ┌─[回测分析]──────┐    │  │ ALERTS     │  │
│  │ SCANNER        │  │  │ 配置 │ 结果     │    │  │ 🔴连败15笔 │  │
│  │  市场扫描      │  │  │                  │    │  │ 🟡回撤18%  │  │
│  └────────────────┘  │  └─────────────────┘    │  └────────────┘  │
│                      │                           │                  │
├──────────────────────┴───────────────────────────┴──────────────────┤
│  底部面板 (25%)                                                      │
│  ┌─[Output]──[Terminal]──[AI Assistant]──[Notifications]──────────┐ │
│  │ 15:30:01 [INF] 日盘收盘，flush 数据                             │ │
│  │ 15:29:58 [INF] quotes=1,234,567 bars=98,765                    │ │
│  └────────────────────────────────────────────────────────────────┘ │
├─────────────────────────────────────────────────────────────────────┤
│ Ln 1, Col 1  UTF-8  ● 已连接 localhost:5199  策略:1  延迟:3ms      │
└─────────────────────────────────────────────────────────────────────┘
```

### ShellView.xaml 核心结构

```xml
<Window>
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>   <!-- MenuBar -->
      <RowDefinition Height="Auto"/>   <!-- Command Bar -->
      <RowDefinition Height="*"/>      <!-- Docking -->
      <RowDefinition Height="Auto"/>   <!-- StatusBar -->
    </Grid.RowDefinitions>

    <!-- AvalonDock -->
    <ad:DockingManager Grid.Row="2"
        DocumentsSource="{Binding Documents}"
        AnchorablesSource="{Binding Tools}">
        <ad:DockingManager.Theme>
            <ad:Vs2013DarkTheme/>
        </ad:DockingManager.Theme>
        <ad:DockingManager.LayoutItemTemplateSelector>
            <!-- 根据 VM 类型选择 View -->
        </ad:DockingManager.LayoutItemTemplateSelector>
    </ad:DockingManager>
  </Grid>
</Window>
```

---

## 3. Docking 抽象层

```csharp
// ── 基类 ──

public abstract class ToolViewModel : ViewModelBase
{
    public string Title { get; set; }
    public string IconSource { get; set; }
    public bool IsVisible { get; set; } = true;
    public DockPosition PreferredPosition { get; set; }
}

public abstract class DocumentViewModel : ViewModelBase
{
    public string Title { get; set; }
    public bool IsSelected { get; set; }
    public bool IsDirty { get; set; }
}
```

### Tool Windows 清单

| Tool | 位置 | 说明 |
|------|:---:|------|
| `ExplorerToolViewModel` | Left | 策略树 + 账户 + 服务器状态 |
| `WatchListToolViewModel` | Right | 自选品种 + 实时报价 |
| `OrderBookToolViewModel` | Right | 五档盘口 |
| `PositionsToolViewModel` | Right | 持仓列表 |
| `AlertsToolViewModel` | Right | 告警中心 |
| `ScannerToolViewModel` | Left | 市场扫描 + 筛选 |
| `OutputToolViewModel` | Bottom | 日志输出 |
| `TerminalToolViewModel` | Bottom | 命令行终端 |
| `AIAssistantToolViewModel` | Bottom | AI 对话 |

### Document 清单

| Document | 说明 |
|----------|------|
| `DashboardDocumentViewModel` | 总览仪表盘 (默认打开) |
| `ChartDocumentViewModel` | K线图 (每品种一个 Tab) |
| `StrategyMonitorDocumentViewModel` | 单策略监控 |
| `BacktestDocumentViewModel` | 回测配置 + 结果 |
| `EditorDocumentViewModel` | C# 策略代码编辑器 |
| `RiskMonitorDocumentViewModel` | 风控面板 |
| `OrderFlowDocumentViewModel` | 订单流监控 |

---

## 4. 布局与 Workspace

### 默认布局

```
左侧:  Explorer, Strategy
中央:  Dashboard
右侧:  WatchList, Positions, Alerts
底部:  Output, Terminal
```

### Workspace 系统

```csharp
public class Workspace
{
    public string Name { get; set; }
    public string LayoutXml { get; set; }     // AvalonDock 序列化
    public List<string> OpenSymbols { get; set; }
    public List<string> OpenDocuments { get; set; }
}
```

### 预置 Workspace

| Workspace | 自动打开 | 用途 |
|-----------|---------|------|
| **期货交易** | Chart(rb2608), WatchList, Positions, OrderBook | 日常盯盘 |
| **回测研究** | Backtest, Chart(rb2005), Output | 策略优化 |
| **策略开发** | Editor(Strategy.cs), Output, Terminal | 写策略代码 |
| **系统监控** | Dashboard, Alerts, Output | 运维值班 |
| **行情分析** | Chart×4, Scanner, WatchList | 多品种对比 |

### 布局持久化

```csharp
// 保存
var serializer = new XmlLayoutSerializer(DockManager);
serializer.Serialize("layout.config");

// 恢复
serializer.Deserialize("layout.config");

// 切换 Workspace
Workspaces.Current = workspaces[1];  // → 恢复该 Workspace 的布局
```

### 多显示器

```xml
<ad:DockingManager CanFloat="True" />
```

拖出浮动窗口 → 多屏独立显示。

---

## 5. Command Palette (Ctrl+P)

```
Ctrl+P 弹出:
┌──────────────────────────────────────────┐
│ > open chart                             │
├──────────────────────────────────────────┤
│  📈 Open Chart          rb2608           │
│  📈 Open Chart          ag2608           │
│  ▶  Run Backtest                        │
│  ▶  Run Strategy        ma-cross        │
│  ⏸  Pause Strategy      ma-cross        │
│  🔌 Connect             SimNow           │
│  📄 New Strategy                        │
│  📊 Export Report                       │
│  🌙 Toggle Theme                        │
│  📋 Switch Workspace    回测研究         │
└──────────────────────────────────────────┘
```

```csharp
public class CommandPaletteViewModel : ViewModelBase
{
    public ObservableCollection<PaletteCommand> AllCommands { get; }
    public ObservableCollection<PaletteCommand> FilteredCommands { get; }
    public string SearchText { get; set; }

    // 模糊搜索
    partial void OnSearchTextChanged(string value)
    {
        FilteredCommands = AllCommands
            .Where(c => c.Name.Contains(value, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}

public record PaletteCommand(
    string Name,
    string Category,
    string Icon,
    Action Execute
);
```

全局注册:

```csharp
// ShellView.xaml
<Window.InputBindings>
    <KeyBinding Key="P" Modifiers="Control"
                Command="{Binding ShowCommandPaletteCommand}" />
    <KeyBinding Key="F1"
                Command="{Binding ShowCommandPaletteCommand}" />
</Window.InputBindings>
```

---

## 6. EventAggregator 消息总线

CommunityToolkit.Mvvm 内置 `WeakReferenceMessenger`。

### 消息定义

```csharp
// 品种选择 → 所有图表联动
public record SymbolSelectedMessage(string Symbol);

// 订单成交 → 持仓/日志/告警 联动
public record OrderExecutedMessage(OrderEvent Fill);

// 策略状态变更 → Explorer/监控面板 联动
public record StrategyStateMessage(string StrategyId, bool IsRunning);

// 回测完成 → 图表/绩效表 联动
public record BacktestCompletedMessage(string ConfigId, EngineReport Report);

// 告警触发 → 告警面板/状态栏 联动
public record AlertTriggeredMessage(MonitorAlert Alert);

// 连接状态 → 状态栏/Explorer 联动
public record ConnectionStateMessage(bool Connected, string Server);

// Workspace 切换 → 所有面板响应
public record WorkspaceChangedMessage(string WorkspaceName);
```

### 使用示例

```csharp
// Explorer 双击策略
WeakReferenceMessenger.Default.Send(
    new SymbolSelectedMessage("rb2608"));

// ChartDocument 订阅
WeakReferenceMessenger.Default.Register<SymbolSelectedMessage>(this,
    (_, msg) => LoadSymbol(msg.Symbol));

// Positions 也订阅
WeakReferenceMessenger.Default.Register<SymbolSelectedMessage>(this,
    (_, msg) => ShowPositions(msg.Symbol));
```

---

## 7. 总览 Dashboard

默认打开的 Document。

```
┌─ Dashboard ────────────────────────────────────────────────────────┐
│                                                                    │
│  ┌────────────────── KPI 卡片 ──────────────────────────────────┐ │
│  │ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌─────┐ │ │
│  │ │ 运行策略  │ │ 今日PnL  │ │ 总权益   │ │ 最大回撤  │ │成交 │ │ │
│  │ │    3     │ │ +12,500  │ │1,250,000 │ │  -3.2%   │ │ 156 │ │ │
│  │ └──────────┘ └──────────┘ └──────────┘ └──────────┘ └─────┘ │ │
│  └──────────────────────────────────────────────────────────────┘ │
│                                                                    │
│  ┌─ 权益曲线 ──────────────────────────────────────────────────┐  │
│  │                                                              │  │
│  │  ▁▂▃▂▁▃▅▇▆▄▂▁▃▅▆▇▆▅▃▂▁▁▂▃▄▅▆▇    +15.2%                │  │
│  │  ▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔  -3.2%                   │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                    │
│  ┌─ 策略状态表 ─────────────────────────────────────────────────┐  │
│  │ 策略         状态    品种     权益    今日PnL   胜率   延迟   │  │
│  │ MaCross      ●运行  rb2608  1,037K   +4,200   23.8%  3ms    │  │
│  │ BollBreak    ⏸暂停  ag2608   450K      0      35.2%  -     │  │
│  │ SpreadArb    ○待启  i2609    100K      0       -     -     │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
```

数据来源: `GET /api/portfolio` + `GET /api/strategies` + SignalR 推送

---

## 8. 策略监控

双击策略表中的行或 Explorer 中的策略打开。

```
┌─ 策略监控: MaCross ────────────────────────────────────────────────┐
│                                                                    │
│  ┌─ 控制栏 ─────────────────────────────────────────────────────┐ │
│  │ [⏸ 暂停] [▶ 恢复] [📄 导出] [⚙ 参数]                        │ │
│  │ rb2608 · SMA(5,20) · 1手 · 资金 100K                         │ │
│  └──────────────────────────────────────────────────────────────┘ │
│                                                                    │
│  ┌─ K线图 ──┬─ 持仓 ──┬─ 今日成交 ──────────────────────────────┐│
│  │ ┃┃▲┃┃   │ rb2608  │ #   时间   方向  价格  手数  PnL        ││
│  │ ┃┃┃┃┃   │ Long 2  │ 41  09:15  Buy  3584   1   +4200       ││
│  │ ▲Buy    │ +4,200  │ 40  09:12  Sell 3578   1    -630       ││
│  │ ▼Sell   │         │                                          ││
│  └─────────┴─────────┴──────────────────────────────────────────┘│
│                                                                    │
│  ┌─ 绩效 ───────────────────────────────────────────────────────┐ │
│  │ 今日PnL  +4,200 │ 胜率 25/41 │ 最大回撤 -3.2% │ 夏普 1.8    │ │
│  └──────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────┘
```

数据来源: `GET /api/strategies/{id}` + SignalR 订单推送

---

## 9. 回测分析

```csharp
public class BacktestDocumentViewModel : DocumentViewModel
{
    // 配置
    public ObservableCollection<string> StrategyTypes { get; }
    public string SelectedStrategy { get; set; }
    public string Symbol { get; set; }
    public int PeriodMinutes { get; set; } = 5;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public decimal Capital { get; set; } = 100000;
    public ObservableCollection<ParameterItem> Parameters { get; }

    // 结果
    public EngineReport? Result { get; set; }
    public PlotModel? EquityModel { get; set; }
    public PlotModel? TradeChartModel { get; set; }
    public ObservableCollection<ComparisonRow> Comparisons { get; }

    // 命令
    public IAsyncRelayCommand RunBacktestCommand { get; }
    public IRelayCommand ExportReportCommand { get; }
    public IRelayCommand AddComparisonCommand { get; }
}

public class ParameterItem
{
    public string Name { get; set; }
    public string Description { get; set; }
    public double Value { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
}
```

### 执行流程

```
用户填配置 → 点 [运行]
  │
  ▼
BacktestRunner.RunAsync(config)    ← 后台 Task, 不阻塞 UI
  │
  ├── BarStore(dbPath)
  ├── HistoricalBarFeed(periodMinutes)
  ├── ExecutionHandler + PortfolioManager
  ├── StrategyFactory.Create → 策略实例
  └── TradingEngine.RunAsync(ct)
         │
         ▼
  EngineReport { EquityCurve, Trades, PerformanceReport }
         │
         ▼
  Dispatcher.Invoke:
    ├── EquityCurve → OxyPlot LineSeries (权益 + 回撤双轴)
    ├── Trades → K线图 Annotation (买卖标记)
    ├── Performance → 绩效 DataGrid
    └── Comparisons → 对比表追加行
```

### 参数对比

```
┌─ 对比组 ───────────────────────────────────┐
│ #  参数         Trades  WinRate  NetProfit │
│ 1  5/20/1        378   23.8%    937,260    │
│ 2  10/40/1       156   35.2%    428,500    │
│ 3  20/60/1        89   42.1%    215,300    │
│                                           │
│ [+ 添加对比]  [导出 CSV]                    │
└───────────────────────────────────────────┘
```

---

## 10. 行情图表

复用现有 `ChartViewModel` (OxyPlot 四窗格)。

```
┌─ Chart: rb2608 ────────────────────────────────────────────────────┐
│ ┌─ 工具栏 ───────────────────────────────────────────────────────┐ │
│ │ [1m] [5m] [15m] [30m] [1h] [1d]  |  ☑MA ☑BOLL ☑MACD ☑RSI    │ │
│ └────────────────────────────────────────────────────────────────┘ │
│                                                                    │
│ ┌─ K线主图 ──────────────────────────────────────────────────────┐│
│ │  ┃┃┃▲┃┃  ┃┃▼┃┃  ┃┃┃┃▲┃    ← CandleStick                    ││
│ │  ─── ← MA5    ─── ← MA20    ─── ← MA60                        ││
│ │  ··· ← BOLL↑   ··· ← BOLL↓                                    ││
│ └────────────────────────────────────────────────────────────────┘│
│ ──────────────── GridSplitter ─────────────────────────────────── │
│ ┌─ 成交量 ───────────────────────────────────────────────────────┐│
│ │  ▆▃▅▂▇▄▁▅▃▆▂▄▇▅▁▃▆▄▂▅▇▃▁▄▆▂▅▃▇▄▁  ← RectangleBar             ││
│ └────────────────────────────────────────────────────────────────┘│
│ ──────────────── GridSplitter ─────────────────────────────────── │
│ ┌─ MACD ─────────────────────────────────────────────────────────┐│
│ │  ─── ← DIF    ─── ← DEA    ▆▃▅▂ ← Histogram                  ││
│ └────────────────────────────────────────────────────────────────┘│
│ ──────────────── GridSplitter ─────────────────────────────────── │
│ ┌─ RSI ──────────────────────────────────────────────────────────┐│
│ │  ─── ← RSI(14)    ─── 70    ─── 30                            ││
│ └────────────────────────────────────────────────────────────────┘│
└────────────────────────────────────────────────────────────────────┘
```

- 已有 `ChartViewModel` 基本不改
- Phase 4 升级 SciChart

---

## 11. 风控与告警

### Alerts 面板 (右侧 Tool)

```
┌─ ALERTS ──────────────────────────┐
│                                   │
│  🔴 连续亏损                       │
│     ma-cross · 连败 15 笔          │
│     建议暂停 · 2 分钟前             │
│                                   │
│  🟡 回撤告警                       │
│     boll-break · 回撤 18.2%       │
│     接近上限 20% · 5 分钟前         │
│                                   │
│  🟡 高滑点                         │
│     ma-cross · 滑点 3.5 > 3.0     │
│     12 分钟前                      │
│                                   │
│  ── 系统 ──                       │
│  🟢 CTP行情 正常                   │
│  🟢 CTP交易 正常                   │
│  🟢 内存 256MB                    │
│                                   │
│  [清除全部] [仅显示严重]            │
└───────────────────────────────────┘
```

数据来源: FeedbackMonitor → SignalR `Alert` 推送

### RiskMonitor Document (风控详情)

```
┌─ 风控监控 ─────────────────────────────────────────────────────────┐
│                                                                    │
│  ┌─ 账户风险 ──┬── 策略风险 ──┬── 系统风险 ──────────────────────┐│
│  │             │              │                                   ││
│  │ 保证金率    │ 最大回撤     │ CPU: 12%  内存: 256MB            ││
│  │ ████████░░ │ ██████░░░░░ │ 延迟: 3ms  重连: 0               ││
│  │   85%      │   15.2%     │                                   ││
│  │             │              │                                   ││
│  │ 当日亏损    │ 持仓集中度   │                                    ││
│  │ -2,500     │ ████░░░░░░  │                                    ││
│  │ (2.5%)     │   38%       │                                    ││
│  └────────────┴─────────────┴───────────────────────────────────┘│
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
```

---

## 12. 订单流

```
┌─ 订单流 ───────────────────────────────────────────────────────────┐
│                                                                     │
│  [委托] [成交] [撤单] [拒单]                     [过滤 ▼] [导出]   │
│                                                                     │
│  ┌─ DataGrid ────────────────────────────────────────────────────┐ │
│  │ 时间    品种    方向   类型   价格    手数   已成交   状态     │ │
│  │ 09:15  rb2608  Buy   Market 3584    2      2       Filled   │ │
│  │ 09:14  rb2608  Sell  Limit  3590    1      0       Waiting  │ │
│  │ 09:12  ag2608  Buy   Stop   6520    1      0       Waiting  │ │
│  │ 09:10  rb2608  Sell  Market 3578    1      1       Filled   │ │
│  └───────────────────────────────────────────────────────────────┘ │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

数据来源: `GET /api/orders` + SignalR 实时推送

---

## 13. 日志与终端

### Output 面板 (底部)

```
┌─ Output ───────────────────────────────────────────────────────────┐
│ [Serilog] [策略日志] [CTP] [回测]                          [清除]  │
│                                                                     │
│ 15:30:01 [INF] 日盘收盘，flush 数据                                │
│ 15:29:58 [INF] quotes=1,234,567 bars=98,765 reconnect=0           │
│ 15:29:45 [WRN] 价差异常: ag2608 Spread=5.20                       │
│ 15:29:30 [INF] [ma-cross] 成交: rb2608 Buy 2手 @ 3584.00          │
│ 15:28:00 [INF] [ma-cross] 金叉做多 @ 3584.00                      │
└─────────────────────────────────────────────────────────────────────┘
```

### Terminal 面板 (底部)

```
┌─ Terminal ─────────────────────────────────────────────────────────┐
│                                                                     │
│ > backtest --config ma-cross-rb.json --mode bar                    │
│ ═══════════════════════════════════                                 │
│   Backtest Report                                                   │
│   Final Equity: ¥1,037,300.96                                      │
│   Trades: 378   WinRate: 23.8%                                     │
│ ═══════════════════════════════════                                 │
│                                                                     │
│ > connect simnow                                                    │
│ ● SimNow 已连接   行情: OK   交易: OK                               │
│                                                                     │
│ > _                                                                 │
└─────────────────────────────────────────────────────────────────────┘
```

内嵌 CLI——和 `TradingStudio.exe` 命令行完全一致。

---

## 14. 策略编辑器

```
┌─ Editor: MaCrossStrategy.cs ───────────────────────────────────────┐
│                                                                     │
│  1  using TradingStudio.Core.Engine;                                │
│  2  using TradingStudio.Core.Models;                                │
│  3                                                                  │
│  4  public class MaCrossStrategy : IStrategy                       │
│  5  {                                                               │
│  6      [StrategyParameter(...)]                                    │
│  7      public int FastPeriod { get; set; } = 5;                   │
│  8                                                                  │
│  9      public void OnBar(Bar bar)                                  │
│ 10      {                                                           │
│ 11          var fastMa = SMA(bar.Close, FastPeriod);                │
│ 12          var slowMa = SMA(bar.Close, SlowPeriod);                │
│ 13          // 金叉做多                                            │
│ 14          if (fastMa > slowMa)                                    │
│ 15              _ctx.MarketBuy(bar.InstrumentId, Quantity);         │
│ 16      }                                                           │
│ 17  }                                                               │
│                                                                     │
├─────────────────────────────────────────────────────────────────────┤
│ Ln 10, Col 25  C#  UTF-8  ● 无错误                                  │
└─────────────────────────────────────────────────────────────────────┘
```

AvalonEdit 提供语法高亮、行号、代码折叠。策略代码编辑后可通过 Command Palette `> Build Strategy` 编译。

---

## 15. 插件系统

```csharp
public interface IPlugin
{
    string Name { get; }
    string Version { get; }
    void Initialize(IPluginContext context);
    void Shutdown();
}

public interface IPluginContext
{
    IServiceProvider Services { get; }
    IMessenger Messenger { get; }
    IDockingManager Docking { get; }
}
```

| 插件 | Tool Windows | Documents |
|------|-------------|-----------|
| CorePlugin | Explorer, WatchList, Alerts | Dashboard, Chart |
| BacktestPlugin | — | BacktestDocument |
| EditorPlugin | — | EditorDocument |
| RiskPlugin | — | RiskMonitorDocument |
| OrderFlowPlugin | — | OrderFlowDocument |
| AIPlugin | — | AIAssistantDocument |
| ScannerPlugin | Scanner | — |

---

## 16. MVVM 代码结构

```
src/TradingStudio.UI/
├── App.xaml / App.xaml.cs
├── Bootstrapper.cs                    ← DI + Plugin 加载
│
├── Views/
│   ├── ShellView.xaml                 ← 主窗口 (AvalonDock)
│   ├── Documents/
│   │   ├── DashboardView.xaml
│   │   ├── ChartView.xaml
│   │   ├── StrategyMonitorView.xaml
│   │   ├── BacktestView.xaml
│   │   ├── EditorView.xaml
│   │   ├── RiskMonitorView.xaml
│   │   └── OrderFlowView.xaml
│   ├── Tools/
│   │   ├── ExplorerToolView.xaml
│   │   ├── WatchListView.xaml
│   │   ├── PositionsView.xaml
│   │   ├── AlertsView.xaml
│   │   ├── ScannerView.xaml
│   │   ├── OutputToolView.xaml
│   │   └── TerminalToolView.xaml
│   └── Dialogs/
│       └── CommandPaletteView.xaml
│
├── ViewModels/
│   ├── ShellViewModel.cs
│   ├── Documents/
│   │   ├── DocumentViewModel.cs       ← 基类
│   │   ├── DashboardViewModel.cs
│   │   ├── ChartViewModel.cs          ← 已有
│   │   ├── StrategyMonitorViewModel.cs
│   │   ├── BacktestViewModel.cs
│   │   ├── EditorViewModel.cs
│   │   ├── RiskMonitorViewModel.cs
│   │   └── OrderFlowViewModel.cs
│   ├── Tools/
│   │   ├── ToolViewModel.cs           ← 基类
│   │   ├── ExplorerToolViewModel.cs
│   │   ├── WatchListViewModel.cs
│   │   ├── PositionsViewModel.cs
│   │   ├── AlertsViewModel.cs
│   │   ├── ScannerViewModel.cs
│   │   ├── OutputToolViewModel.cs
│   │   └── TerminalToolViewModel.cs
│   ├── CommandPaletteViewModel.cs
│   └── WorkspaceViewModel.cs
│
├── Models/
│   ├── Workspace.cs
│   ├── PaletteCommand.cs
│   └── ParameterItem.cs
│
├── Services/
│   ├── EngineApiClient.cs             ← REST + SignalR
│   ├── BacktestRunner.cs              ← 本地回测
│   ├── WorkspaceService.cs
│   ├── DockingService.cs
│   └── PluginLoader.cs
│
├── Converters/
│   └── ...                            ← 已有
│
└── Plugins/
    ├── IPlugin.cs
    ├── IPluginContext.cs
    └── CorePlugin.cs
```

---

## 17. 分阶段实施

### Phase A: AvalonDock 骨架 (2-3天)

- AvalonDock + Vs2013DarkTheme 集成
- ShellViewModel: Documents + Tools 集合管理
- `ToolViewModel` / `DocumentViewModel` 抽象基类
- Dashboard 最小实现 (KPI 卡片 + 策略状态表)
- Explorer Tool (策略树 + 服务器状态)
- 对接 `GET /api/health` + `GET /api/portfolio`

### Phase B: 核心面板 (2-3天)

- ChartView 集成 (复用现有 ChartViewModel)
- WatchList + Positions + Alerts 面板
- SignalR 实时推送接入
- Output 日志面板
- Command Palette (Ctrl+P)
- Workspace 切换 + 布局保存

### Phase C: 回测集成 (2-3天)

- BacktestView: 配置表单 + 结果展示
- 参数反射 (从 [StrategyParameter] 生成表单)
- BacktestRunner 内嵌引擎
- 权益曲线 + 交易标记 + 绩效表
- 参数对比表

### Phase D: 完善 (2-3天)

- EditorView (AvalonEdit 代码编辑器)
- TerminalView (CLI 终端)
- Plugin 加载器
- 多显示器浮动窗口
- 风控 + 订单流 Document

---

## 18. 技术栈决策

| 层次 | 选型 | 理由 |
|------|------|------|
| 布局框架 | **AvalonDock** | 两份参考一致, Document/Tool 双轨 |
| 主题 | **Vs2013DarkTheme** | 自带, 接近 VSCode |
| MVVM | **CommunityToolkit.Mvvm** | 已有, 源码生成 |
| 图表 | **OxyPlot** (Phase 1-3) | 已有, 四窗格复用 |
| 图表 (Phase 4) | **SciChart** | 专业金融图表 |
| 消息总线 | **WeakReferenceMessenger** | CommunityToolkit.Mvvm 内置 |
| 代码编辑 | **AvalonEdit** | VSCode文档推荐 |
| DI | **Microsoft.Extensions.DI** | 已有 |
| 插件 | **自定义 IPlugin** | 轻量, 够用 |

### 18.2 SignalR 实时推送事件

```csharp
// 引擎 → UI 的 7 个推送事件
TickSnapshot        ← 每秒, 全品种最新行情
PortfolioUpdated    ← 每秒, 权益/现金/保证金/持仓
StrategiesUpdated   ← 每5秒, 策略快照列表
OrderUpdated        ← 实时, 单笔成交/拒绝 (按策略分组)
OrderFlowUpdated    ← 实时, 所有成交流水
Alert               ← 实时, 告警 (按策略分组)
AlertsUpdated       ← 每3秒, 告警全量列表
```

实现: `EngineHubPushService` (BackgroundService) — 监控 `FillChannel` + `TickSnapshot` + `FeedbackMonitor`, 通过 `IHubContext<EngineHub>` 推送。

> **2026-06-14 决策**:
> - OxyPlot 主力, SciChart 作为 Phase 4 升级路径
> - 使用 CommunityToolkit.Mvvm 内置 Messenger, 不引入额外 EventAggregator
> - 回测在客户端本地执行 (内嵌 TradingEngine), 不依赖服务器
> - WPF 原生控件优先, HandyControl 推迟 Phase 4
> - 绩效面板: OxyPlot 统一 (不以 LiveCharts2 增加依赖)

---

## 附录 A: 图表库选型依据

> 详细评估见原 `13-ui-technology-selection.md`，此处保留关键结论。

### A.1 WPF vs Blazor Server

| 维度 | WPF | Blazor Server |
|------|-----|---------------|
| 延迟 | 亚毫秒（本地） | 10-50ms（网络 + DOM） |
| 实时推送 | 内存事件，零拷贝 | SignalR 序列化 |
| 开发体验 | XAML + C#，20 年舒适区 | 需学 Web 前端 |
| 离线能力 | 本地运行 | 需浏览器 |

**选择 WPF** — 实时行情对延迟敏感，XAML 是舒适区。

### A.2 图表库评估

| 维度 | OxyPlot | SciChart | FancyCandles |
|------|:---:|:---:|:---:|
| 授权 | MIT | $1,095/年 | GPL-3.0 |
| MVVM | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| K线功能 | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| 性能 | CPU 渲染 | GPU 加速 | WPF 原生 |
| 社区 | 千万下载 | 企业级 | 126 star |

**OxyPlot 主力** — MVVM 最干净，MIT 免费，Phase 2-3 够用。Phase 4 如需升级 → SciChart/ProEssentials。

### A.3 淘汰方案

| 方案 | 淘汰原因 |
|------|---------|
| ScottPlot 5 | 官方不支持 MVVM，"自己写 UserControl"是架构债 |
| LiveCharts2 | 金融 K 线太弱，只适合绩效面板 |
| 自绘 SkiaSharp | 造轮子 3 个月+ |

### A.4 UI 组件库

**WPF 原生控件 (Phase 2-3) → HandyControl (Phase 4)**

OxyPlot 一个库画所有图表，WPF DataGrid/Button/TreeView 原生够用。HandyControl 提供深色主题和 80+ 控件，但增加依赖，Phase 4 按需引入。

---

## 附录 B: K 线图数据流

```
回测模式                          实盘模式
────────                          ────────
BarStore.QueryBarsAsync()         CtpLiveFeed → TickEvent
    │                                  │
    ▼                                  ▼
Bar[] → HighLowItem[] 转换        BarAggregator → BarEvent
    │                                  │
    ▼                                  ▼
ChartViewModel.KLineModel         ChartViewModel.KLineModel
  = new PlotModel {                (增量更新最后一根 Bar)
    Series = {
      CandleStickSeries,          SignalR Hub → EngineHubClient
      LineSeries (MA overlay),        │
      VolumeSeries,                   ▼
      MacdSeries,                ObservableCollection 增量更新
      RsiSeries                      │
    }                                ▼
  }                            PlotModel.InvalidatePlot(true)
    │
    ▼
oxy:PlotView.Model="{Binding KLineModel}"
```

ViewModel 不持有 UI 引用 — `PlotModel` 是纯数据对象，可单元测试。切换图表库 = 改 View 层 XAML + Adapter，2-3 天。
