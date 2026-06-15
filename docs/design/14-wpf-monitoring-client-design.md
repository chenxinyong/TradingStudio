# 14 — WPF 监控客户端设计

> 量化交易系统的 WPF 桌面监控客户端。Dashboard-first 架构，SignalR 实时推送 + 系统托盘告警。
>
> **版本**: v1.0 | **日期**: 2026-06-15 | **状态**: 设计中，Phase 1-3 待实施
>
> **设计原则** (第一性原理验证):
> 1. Dashboard-first — 一个主面板看到全局，异常可钻入详情
> 2. SignalR-only — 实时数据走 SignalR。health.json 为降级路径
> 3. OxyPlot-only — Phase 2-3 只用 OxyPlot，其他推迟 Phase 4
> 4. Layer 0→3 — 先展示已有数据，逐步接入引擎实时数据
> 5. 告警核心化 — 系统托盘 + 桌面通知优先于图表渲染

---

## 1. 窗口架构

```
TradingStudio.UI.exe
├── System Tray (NotifyIcon)              ← 始终在系统托盘
│   ├── 连接状态指示 (绿/黄/红)
│   ├── 告警 → Windows 桌面通知
│   └── 右键: 打开仪表盘 / 打开K线 / 退出
│
├── DashboardWindow (主窗口)              ← 启动即打开
│   ┌────────────────┬────────────────┐
│   │  系统健康       │  风控概览       │
│   ├────────────────┴────────────────┤
│   │  活跃告警 (最近 N 条)            │
│   ├────────────────┬────────────────┤
│   │  策略状态       │  最近成交       │
│   └────────────────┴────────────────┘
│
├── ChartWindow (K线图)                   ← 按需打开
├── StrategyDetailWindow                  ← 双击策略打开
└── OrderHistoryWindow                    ← 双击成交打开
```

## 2. 功能清单 (7 模块, 28 项)

### 2.1 Dashboard 主面板

| 功能 | 数据源 |
|------|--------|
| 连接状态指示 (●已连接/◉降级/○断开) | SignalR StateChanged |
| 当前交易时段 + 距开盘倒计时 | SessionScheduler |
| Tick 行情计数 | SignalR TickSnapshot |
| 已连接品种数 | SignalR TickSnapshot |
| 运行时长 | 客户端计时 |
| 总权益 / 可用资金 / 保证金占用率 | SignalR PortfolioUpdated |
| 持仓品种数 / 累计盈亏 | SignalR PortfolioUpdated |
| 活跃告警列表 (最近 100 条) | SignalR AlertsUpdated |
| 策略状态卡片 | SignalR StrategiesUpdated |
| 最近成交列表 (最近 50 笔) | SignalR OrderUpdated |

### 2.2 K 线图表 (ChartWindow)

| 功能 | 数据源 |
|------|--------|
| 4 窗格 (K线+成交量+MACD+RSI) | OxyPlot |
| 品种选择器 | FutureRegistry |
| 历史 Bar 加载 | SQLite 直读 |
| 实时 Tick → Bar 更新 | SignalR TickSnapshot |
| 指标叠加切换 (MA/BOLL/MACD/RSI) | OxyPlot |
| X 轴缩放同步 (4 窗格联动) | OxyPlot AxisChanged |
| 窗口自动滚动 | — |

### 2.3 策略详情 (StrategyDetailWindow)

| 功能 | 数据源 |
|------|--------|
| 策略基本信息 (名称/类型/版本/分配资金) | REST GET /api/strategies/{id} |
| 策略参数展示 | StrategyConfig |
| 策略状态 (运行中/已暂停) | SignalR StrategiesUpdated |
| 持仓明细 | PortfolioManager |
| 权益曲线 | PerformanceReport.EquityCurve |
| Pause / Resume 控制 | REST POST |

### 2.4 订单与成交历史 (OrderHistoryWindow)

| 功能 | 数据源 |
|------|--------|
| 活跃订单列表 | REST GET /api/orders |
| 订单历史 | REST GET /api/orders |
| 成交记录 (含手续费/滑点) | REST GET /api/trades |
| 按策略过滤 | 客户端 |

### 2.5 系统托盘

| 功能 |
|------|
| 托盘图标 (●绿/●黄/●红) |
| 悬浮提示 (时段 · 权益 · 告警数) |
| 桌面通知 (Severity ≥ Warning) |
| 右键菜单 (仪表盘/K线图/退出) |

### 2.6 回测分析

| 功能 | 数据源 |
|------|--------|
| 权益曲线图 | PerformanceReport.EquityCurve |
| 回撤曲线图 | PerformanceReport.Drawdown |
| 绩效指标卡片 (Sharpe/Sortino/胜率/盈亏比) | PerformanceReport |
| 交易标记图 (K线标注买卖点) | PerformanceReport.Trades |

### 2.7 降级模式

| 功能 | 触发条件 |
|------|---------|
| health.json 直读 | SignalR 断开 |
| SQLite 直读 Bar | SignalR 断开 |

## 3. 通信架构

```
TradingStudio.exe (localhost:5199)       TradingStudio.UI.exe
┌──────────────────────────────┐         ┌─────────────────────────┐
│ EngineHubPushService (1s)    │──WS──→  │ EngineHubClient          │
│  ├─ TickSnapshot             │         │  ├─ OnTickSnapshot()     │
│  ├─ PortfolioUpdated         │         │  ├─ OnPortfolioUpdated() │
│  ├─ StrategiesUpdated        │         │  ├─ OnStrategiesUpdated()│
│  ├─ OrderUpdated (event)     │         │  ├─ OnOrderUpdated()     │
│  └─ Alert (event)            │         │  └─ OnAlert()            │
│                              │         │                          │
│ EngineHub (Hub Methods)      │◄─invoke─│  ├─ SubscribeStrategy()  │
│ EngineMonitorApi (REST)      │◄─GET─── │  └─ 降级轮询 (Layer 0)  │
└──────────────────────────────┘         └─────────────────────────┘
                                                 │
                                          Dispatcher.Invoke
                                                 │
                                          ViewModels ──→ Views
```

## 4. 项目结构 (新增/变更)

```
src/TradingStudio.UI/
├── ViewModels/
│   ├── ViewModelBase.cs                 ← 新: INavigationAware
│   ├── DashboardViewModel.cs            ← 新: 4 面板数据
│   ├── ChartViewModel.cs                ← 改: 抽取 ChartFactory
│   ├── StrategyDetailViewModel.cs       ← 新
│   └── OrderHistoryViewModel.cs         ← 新
│
├── Services/
│   ├── EngineHubClient.cs               ← 新: SignalR 连接 + 事件路由
│   ├── ChartFactory.cs                  ← 新: 从 ChartViewModel 抽取
│   ├── DataSimulator.cs                 ← 保留: Demo 模式
│   └── BacktestChartAdapter.cs          ← 改: 修复 Engine 引用
│
├── Converters/
│   ├── RunningConverters.cs             ← 保留
│   ├── SeverityToColorConverter.cs      ← 新
│   └── PnLToBrushConverter.cs           ← 新
│
└── Views/
    ├── DashboardView.xaml               ← 新
    ├── StrategyDetailView.xaml          ← 新
    └── OrderHistoryView.xaml            ← 新
```

## 5. 实施计划

### Phase 1: 地基 (2h)

| # | 任务 | 时间 |
|---|------|------|
| 1.1 | 修复 .csproj (加 Engine 引用) + 抽取 ChartFactory | 30min |
| 1.2 | 实现 EngineHubClient SignalR 服务 | 1h |
| 1.3 | ViewModelBase + INavigationAware | 15min |
| 1.4 | App.xaml.cs DI 扩展 | 15min |

**验证**: `dotnet build` 通过；`EngineHubClient.ConnectAsync()` 连上引擎

### Phase 2: Dashboard (3.5h)

| # | 任务 | 时间 |
|---|------|------|
| 2.1 | MainWindow → Dashboard 4 面板布局 | 1h |
| 2.2 | DashboardViewModel (SignalR 事件 → ObservableCollection) | 2h |
| 2.3 | Layer 0 降级模式 (health.json 文件直读) | 30min |

**验证**: Dashboard 实时显示 Tick/权益/策略/告警；引擎断开 → 显示降级

### Phase 3: 详情窗口 + 托盘 (3.5h)

| # | 任务 | 时间 |
|---|------|------|
| 3.1 | ChartWindow 改造 (InstrumentId 可绑定 + 历史Bar + 实时) | 1.5h |
| 3.2 | StrategyDetailWindow | 1h |
| 3.3 | 系统托盘 (NotifyIcon + 桌面通知 + 右键菜单) | 1h |

**验证**: Dashboard → 打开 ChartWindow → 选择品种 → K线渲染；告警 → 桌面通知

### 不做（明确推迟）

| 功能 | 原因 |
|------|------|
| TightenRisk / ClosePosition / ReloadConfig | 服务端为 Phase 3 桩 |
| LiveCharts2 仪表/饼图 | Phase 4 (OxyPlot 够用) |
| HandyControl 主题 | Phase 4 (WPF 原生控件够用) |

## 6. 关联文档

- [13 — UI 技术选型](13-ui-technology-selection.md)
- [12 — 实盘部署设计](12-live-deployment-design.md)
- [11 — 实施路线图 v2](11-implementation-roadmap-v2.md)
- [01 — 第一性原理分析](01-first-principles-analysis.md)
