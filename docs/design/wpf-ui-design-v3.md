# TradingStudio WPF 量化终端 — 设计方案 v3

> 合并两份参考文档:
> - [WPF量化交易界面设计](./WPF量化交易界面设计.md) — AvalonDock 工作台
> - [VSCode风格量化终端设计](./VSCode风格量化终端设计.md) — Command Palette + EventAggregator + Workspace + Plugin
>
> 目标体验: VS Code × Bloomberg Terminal × TradingView Desktop

---

## 1. 整体架构

```
┌─ MenuBar ───────────────────────────────────────────────────────────┐
│ 文件  编辑  视图  运行  工具  帮助                                    │
├─ Command Bar ───────────────────────────────────────────────────────┤
│ [🔍 输入品种代码...]  [回测 ▼] [策略 ▼] [连接]  |  ● SimNow 模拟盘   │
├─────────────────────────────────────────────────────────────────────┤
│            │                                   │                   │
│  左侧      │        中央 Document 区            │   右侧            │
│  Tool      │        (可拖拽 Tab)               │   Tool            │
│  Windows   │                                   │   Windows         │
│            │                                   │                   │
│ Explorer   │  Chart(rb2608)  Backtest  Editor  │ WatchList         │
│ Strategy   │  ┌─────────────────────────────┐  │ OrderBook         │
│ Scanner    │  │  K线图 / 权益曲线 / 代码     │  │ Positions         │
│            │  └─────────────────────────────┘  │ Alerts            │
│            │                                   │                   │
├────────────┴───────────────────────────────────┴───────────────────┤
│  底部面板                                                           │
│  [Output] [Terminal] [AI Assistant] [Notifications]                 │
├─────────────────────────────────────────────────────────────────────┤
│  Ln 1, Col 1  |  UTF-8  |  ● 已连接  |  策略: 1   |  延迟: 3ms      │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 2. 核心设计变更 (v2 → v3)

| v2 | v3 | 来源 |
|----|----|------|
| TabControl 页签 | AvalonDock Document/Tool 体系 | VSCode文档 |
| ViewModel 直接引用 | EventAggregator 消息总线 | VSCode文档 |
| 单一布局 | Workspace 多布局切换 | VSCode文档 |
| 硬编码模块 | IPlugin 插件架构 | VSCode文档 |
| OxyPlot | OxyPlot (Phase 1-3) → SciChart (Phase 4) | VSCode文档 |
| 无命令系统 | Command Palette (Ctrl+P) | VSCode文档 |
| 单一主题 | Vs2013DarkTheme | AvalonDock内置 |

---

## 3. Docking 抽象层

```csharp
// 所有可停靠内容的基类
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
    public bool IsDirty { get; set; }  // 未保存标记
}
```

### Tool Windows（固定面板）

| Tool | 位置 | 说明 |
|------|:---:|------|
| Explorer | Left | 策略树 + 账户 + 服务器状态 |
| WatchList | Right | 自选品种列表 + 实时报价 |
| OrderBook | Right | 五档盘口 |
| Positions | Right | 当前持仓 |
| Alerts | Right | 实时告警 |
| Scanner | Left | 市场扫描 |

### Documents（中央工作区）

| Document | 说明 |
|----------|------|
| ChartDocument | K线图（每品种一个Tab） |
| BacktestDocument | 回测配置 + 结果 |
| EditorDocument | C# 策略代码编辑器 |
| DashboardDocument | 总览仪表盘 |
| AIAssistantDocument | AI 对话 |

```csharp
public class ChartDocumentViewModel : DocumentViewModel
{
    public string Symbol { get; set; }
    public PlotModel KLineModel { get; set; }
}

public class BacktestDocumentViewModel : DocumentViewModel
{
    public StrategyConfig Config { get; set; }
    public EngineReport? Result { get; set; }
}
```

---

## 4. 布局系统

### 默认布局

```
左侧 Tools:     Explorer, Strategy
中央 Documents:  Dashboard
右侧 Tools:     WatchList, Positions, Alerts
底部:           Output, Terminal
```

### 预置 Workspace

```csharp
public class Workspace
{
    public string Name { get; set; }        // "期货交易", "回测研究", "策略开发"
    public string LayoutXml { get; set; }    // AvalonDock 序列化
    public List<string> OpenSymbols { get; set; }
}

// ShellViewModel
public ObservableCollection<Workspace> Workspaces { get; }
public ICommand SwitchWorkspaceCommand { get; }
```

| Workspace | 默认打开 |
|-----------|---------|
| 期货交易 | Chart(rb2608), WatchList, Positions, OrderBook |
| 回测研究 | Backtest, Chart(rb2005), Output(回测日志) |
| 策略开发 | Editor(Strategy.cs), Output(编译), Terminal |
| 系统监控 | Dashboard, Alerts, Logs |

### 布局保存

```xml
<!-- ShellView.xaml -->
<ad:DockingManager x:Name="DockManager"
    DocumentsSource="{Binding Documents}"
    AnchorablesSource="{Binding Tools}">
    <ad:DockingManager.Theme>
        <ad:Vs2013DarkTheme/>
    </ad:DockingManager.Theme>
</ad:DockingManager>
```

```csharp
// 保存
var serializer = new XmlLayoutSerializer(DockManager);
serializer.Serialize("layout.config");

// 恢复
serializer.Deserialize("layout.config");
```

---

## 5. Command Palette (Ctrl+P)

VSCode/Bloomberg 的核心交互。

```
Ctrl+P 弹出:
┌──────────────────────────────────────┐
│ >                                    │
├──────────────────────────────────────┤
│  > Open Chart                        │
│  > Run Backtest                      │
│  > Connect SimNow                    │
│  > New Strategy                      │
│  > Open Symbol    rb2608             │
│  > Pause Strategy ma-cross           │
│  > Export Report                     │
│  > Toggle Dark Mode                  │
└──────────────────────────────────────┘
```

```csharp
public class CommandPaletteViewModel
{
    public ObservableCollection<PaletteCommand> Commands { get; }
    public string SearchText { get; set; }
    public ICommand ExecuteCommand { get; }
}

public record PaletteCommand(string Name, string Category, Action Execute);
```

全局快捷键注册：

```csharp
// ShellView
KeyBindings.Add(new KeyBinding(
    ShowCommandPaletteCommand,
    Key.P, ModifierKeys.Control));
```

---

## 6. EventAggregator 消息总线

ViewModel 不直接引用，通过消息通信。

```csharp
// 事件定义
public record SymbolSelectedEvent(string Symbol);
public record OrderExecutedEvent(OrderEvent Fill);
public record StrategyStateChangedEvent(string StrategyId, bool IsRunning);
public record BacktestCompletedEvent(string ConfigId, EngineReport Report);
public record AlertTriggeredEvent(MonitorAlert Alert);
public record ConnectionStateChangedEvent(bool Connected);

// 发布
_eventBus.Publish(new SymbolSelectedEvent("rb2608"));

// 订阅
_eventBus.Subscribe<SymbolSelectedEvent>(OnSymbolSelected);
```

联动示例：

```
Explorer 双击策略 "ma-cross"
  → Publish(StrategySelectedEvent("ma-cross-rb"))
      │
      ├→ ChartDocument 订阅 → 加载该策略的K线图
      ├→ Positions 订阅 → 显示该策略持仓
      └→ Output 订阅 → 显示策略日志
```

---

## 7. 回测集成

回测是 Document 的一种，配置 → 执行 → 结果在一个 Tab 内完成。

```
BacktestDocument
┌─ 工具栏 ──────────────────────────────────────────────────────────┐
│ [▶ 运行] [⏹ 停止] [📄 导出] [📋 复制参数]  |  状态: 运行中...    │
├─ 配置 (左侧40%) ──────────────┬─ 结果 (右侧60%) ──────────────────┤
│                               │                                   │
│ 策略  [MaCross          ▼]   │ ┌─ 权益曲线 ──────────────────┐  │
│ 品种  [rb2005           ▼]   │ │  ▁▂▃▅▆▇▆▄▂   +937%        │  │
│ 周期  [5min ▼]               │ │  ▔▔▔▔▔▔▔▔▔▔  -0.16%       │  │
│ 起始  [2020-01-02  📅]       │ └────────────────────────────┘  │
│ 结束  [2020-02-28  📅]       │                                   │
│                               │ ┌─ K线 + 交易标记 ────────────┐  │
│ ── 参数 ──                   │ │  ┃▲┃┃  ┃▼┃┃  ┃▲┃┃        │  │
│ FastPeriod  [5     ]          │ │  ▲ Buy  ▼ Sell              │  │
│ SlowPeriod  [20    ]          │ └────────────────────────────┘  │
│ Quantity    [1     ]          │                                   │
│                               │ ┌─ 绩效 ───────────────────────┐ │
│ ── 对比组 ──                 │ │ Trades 378  WR 23.8%         │ │
│ #  参数        Trades  WR    │ │ Profit 937K MDD 0.16%        │ │
│ 1  5/20/1      378  23.8%   │ │ Alerts 2  Fee 2,694          │ │
│ 2  10/40/1     156  35.2%   │ └──────────────────────────────┘  │
│ [+ 添加]                      │                                   │
└───────────────────────────────┴───────────────────────────────────┘
```

---

## 8. 插件系统

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
    IEventAggregator Events { get; }
    IDockingManager Docking { get; }
}
```

| 插件 | Tool Windows | Documents |
|------|-------------|-----------|
| FuturesPlugin | WatchList, OrderBook, Positions | Chart |
| BacktestPlugin | — | BacktestDocument |
| EditorPlugin | — | EditorDocument |
| AIPlugin | — | AIAssistantDocument |
| ScannerPlugin | Scanner | — |

---

## 9. ShellViewModel 结构

```csharp
public class ShellViewModel : ViewModelBase
{
    // Docking
    public ObservableCollection<DocumentViewModel> Documents { get; }
    public ObservableCollection<ToolViewModel> Tools { get; }

    // Workspace
    public ObservableCollection<Workspace> Workspaces { get; }
    public Workspace CurrentWorkspace { get; set; }

    // Command Palette
    public ICommand ShowCommandPaletteCommand { get; }

    // 状态
    public bool IsConnected { get; set; }
    public string StatusText { get; set; }
    public string ActiveStrategy { get; set; }
}
```

---

## 10. 分阶段实施

### Phase A: AvalonDock 骨架 (2天)

- AvalonDock + Vs2013DarkTheme
- ShellViewModel: Documents + Tools 集合
- ToolViewModel / DocumentViewModel 抽象基类
- 默认布局 + Workspace 切换
- Explorer + Dashboard 最小实现

### Phase B: 核心面板 (2天)

- ChartDocument: 复用现有 ChartViewModel
- WatchList + Positions 面板
- Output + Terminal 面板
- EventAggregator 消息总线
- Command Palette (Ctrl+P)

### Phase C: 回测集成 (2天)

- BacktestDocument: 配置 + 结果
- 内嵌引擎执行 (BacktestRunner)
- 权益曲线 + 交易标记
- 参数对比

### Phase D: 完善 (2天)

- EditorDocument (AvalonEdit 代码编辑器)
- 布局保存/恢复
- 插件加载器
- AIAssistant (Phase 4)

---

## 11. 技术栈

| 层次 | 选型 | 来源 |
|------|------|------|
| 布局 | AvalonDock + Vs2013DarkTheme | 两份参考文档一致 |
| MVVM | CommunityToolkit.Mvvm | 已有 |
| 图表 | OxyPlot (Phase 1-3), SciChart (Phase 4) | 现有 + VSCode文档 |
| 消息 | CommunityToolkit.Mvvm Messenger | 替代 EventAggregator |
| 代码编辑 | AvalonEdit | VSCode文档 |
| DI | Microsoft.Extensions.DI | 已有 |

> **2026-06-14 决策**: OxyPlot 主力，SciChart 作为 Phase 4 升级路径。CommunityToolkit.Mvvm 的 `WeakReferenceMessenger` 已内置消息总线，无需额外引入 EventAggregator 库。
