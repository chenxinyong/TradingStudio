---
title: "VSCode风格量化终端设计"
source: "https://chatgpt.com/c/6a2eb237-3ecc-83ec-a501-3577bacb442b"
author:
published:
created: 2026-06-14
description: "ChatGPT conversation with 2 messages"
tags:
  - "clippings"
---
vsCode风格，帮我按照 MVVM + Docking（AvalonDock）架构集成成接近 Bloomberg / 同花顺专业版 / TradingView Desktop 的桌面量化终端界面

---

如果你的目标是 **VS Code 风格 + MVVM + AvalonDock** ，同时达到 **Bloomberg Terminal、同花顺专业版、TradingView Desktop** 这种专业金融终端的体验，那么建议不要把 AvalonDock 当成单纯的停靠控件，而是把它作为整个 Workbench（工作台）框架。

整体架构应该类似：

```
┌─────────────────────────────────────────────────────────────┐
│ MenuBar                                                     │
├─────────────────────────────────────────────────────────────┤
│ Toolbar                                                     │
├─────────────────────────────────────────────────────────────┤
│ Symbol Search | Workspace Selector | Account Status         │
├─────────────┬───────────────────────────────┬───────────────┤
│             │                               │               │
│ Explorer    │        Document Area          │ WatchList     │
│             │                               │               │
│ Strategy    │   K线图 / 回测 / 代码编辑器      │ OrderBook     │
│ Screener    │                               │ Trades        │
│             │                               │               │
├─────────────┴───────────────────────────────┴───────────────┤
│ Output / Log / Terminal / AI Assistant                      │
└─────────────────────────────────────────────────────────────┘
```

---

```
QuantTerminal
│
├─App
│   ├─App.xaml
│   └─Bootstrapper.cs
│
├─Views
│   ├─ShellView.xaml
│   ├─Charts
│   ├─Editors
│   ├─Panels
│   └─Dialogs
│
├─ViewModels
│   ├─ShellViewModel.cs
│   ├─Docking
│   │    ├─DocumentViewModel.cs
│   │    ├─ToolViewModel.cs
│   │    └─LayoutViewModel.cs
│   │
│   ├─ChartViewModel.cs
│   ├─StrategyViewModel.cs
│   ├─WatchListViewModel.cs
│   └─OrderBookViewModel.cs
│
├─Models
│   ├─Symbol.cs
│   ├─Order.cs
│   ├─Position.cs
│   └─Trade.cs
│
├─Services
│   ├─MarketDataService.cs
│   ├─TradingService.cs
│   ├─WorkspaceService.cs
│   └─DockingService.cs
│
├─Infrastructure
│   ├─MVVM
│   ├─EventAggregator
│   ├─DI
│   └─Themes
│
└─Plugins
```

---

## 二、核心 Docking 架构

Bloomberg 和 VSCode 最大特点：

### Tool Window

固定工具窗口

```
Explorer
WatchList
Strategy
Scanner
News
OrderBook
Account
```

对应：

```
public abstract class ToolViewModel : ViewModelBase
{
    public string Title { get; set; }

    public string IconSource { get; set; }

    public bool IsVisible { get; set; }
}
```

---

### Document Window

中央区域

```
Chart
Code Editor
Backtest Result
AI Chat
Research Report
```

对应：

```
public abstract class DocumentViewModel : ViewModelBase
{
    public string Title { get; set; }

    public bool IsSelected { get; set; }
}
```

例如：

```
public class ChartDocumentViewModel : DocumentViewModel
{
    public string Symbol { get; set; }
}
```

---

## 三、ShellView

主窗口只保留一个 DockingManager

```xml
<ad:DockingManager
    DocumentsSource="{Binding Documents}"
    AnchorablesSource="{Binding Tools}">

    <ad:DockingManager.Theme>
        <ad:Vs2013DarkTheme/>
    </ad:DockingManager.Theme>

</ad:DockingManager>
```

其中：

```
public ObservableCollection<DocumentViewModel> Documents { get; }

public ObservableCollection<ToolViewModel> Tools { get; }
```

---

## 四、Bloomberg 风格布局

启动后自动恢复布局：

```
左侧
 ├ Explorer
 ├ Strategy
 └ Scanner

中间
 ├ Chart(BTCUSDT)
 ├ Chart(AAPL)
 ├ Editor(Strategy.cs)

右侧
 ├ WatchList
 ├ OrderBook
 └ Positions

底部
 ├ Output
 ├ Logs
 ├ AI Assistant
 └ Terminal
```

布局保存：

```
XmlLayoutSerializer serializer =
    new XmlLayoutSerializer(dockingManager);

serializer.Serialize("layout.config");
```

恢复：

```
serializer.Deserialize("layout.config");
```

类似 VSCode Workspace。

---

## 五、TradingView 风格图表区域

建议不要自己画 K 线。

直接：

### 方案1（推荐）

SciChart

```
性能★★★★★
金融图表★★★★★
Level2★★★★★
```

适合专业终端。

---

### 方案2

LiveCharts2

```
性能★★★★
开发效率★★★★★
```

适合中小项目。

---

图表窗口：

```
Document
 └ ChartDocument
      ├ K线
      ├ MACD
      ├ RSI
      ├ 成交量
      └ DrawingTool
```

一个图表对应一个 Dock Document。

---

## 六、VSCode 风格命令系统

Bloomberg 和 VSCode 都有 Command Palette。

快捷键：

```
Ctrl+P
```

弹出：

```
> Open Symbol
> Open Chart
> Run Strategy
> New Backtest
> Connect Broker
```

MVVM：

```
public ICommand ShowCommandPaletteCommand { get; }
```

---

## 七、工作区 Workspace

TradingView Desktop 的核心。

```
Workspace
│
├ Layout
├ OpenCharts
├ WatchLists
├ Indicators
├ Accounts
└ Settings
```

模型：

```
public class Workspace
{
    public string Name { get; set; }

    public string LayoutXml { get; set; }

    public List<string> Symbols { get; set; }
}
```

切换：

```
Workspace
 ├ 股票
 ├ 期货
 ├ 加密货币
 └ 量化研究
```

---

## 八、消息总线

不要 ViewModel 相互引用。

使用 EventAggregator：

```
SymbolSelectedEvent
OrderExecutedEvent
ChartOpenedEvent
StrategyStartedEvent
```

例如：

```
_eventBus.Publish(
    new SymbolSelectedEvent("BTCUSDT"));
```

图表自动响应：

```
_eventBus.Subscribe<SymbolSelectedEvent>(
    OnSymbolSelected);
```

---

## 九、插件架构（Bloomberg级）

后期一定会需要。

```
Plugins
│
├ FuturesPlugin
├ StockPlugin
├ CryptoPlugin
├ AIPlugin
├ NewsPlugin
└ BacktestPlugin
```

接口：

```
public interface IPlugin
{
    string Name { get; }

    void Initialize();
}
```

启动时：

```
PluginLoader.Load();
```

采用 或自定义插件加载器。

---

## 十、最终推荐的专业终端架构

```
WPF (.NET 8)

MVVM Toolkit
       +
CommunityToolkit.Mvvm
       +
Autofac
       +
AvalonDock
       +
SciChart
       +
EventAggregator
       +
Workspace Manager
       +
Plugin System
```

界面层次：

```
Shell
 └ DockingManager
      ├ Tool Windows
      │    ├ Explorer
      │    ├ WatchList
      │    ├ Scanner
      │    └ OrderBook
      │
      ├ Documents
      │    ├ Chart
      │    ├ Editor
      │    ├ Backtest
      │    └ AI Chat
      │
      └ Bottom Panels
           ├ Output
           ├ Logs
           ├ Terminal
           └ Notifications
```

这样做出来的交互模式会非常接近：

而且后续接入行情、策略回测、AI分析师、Level2盘口、期权链等模块时，整体架构不会推倒重来。