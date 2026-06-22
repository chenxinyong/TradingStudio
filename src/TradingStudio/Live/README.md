# Live/ — 实盘引擎

## 启动序列

```
dotnet TradingStudio.dll live
  │
  ▼
Program.RunLiveAsync()
  │
  ├─ 1. 加载配置
  │     appsettings.json + appsettings.local.json
  │     ✓ Live:MdFront (行情前置, 必填)
  │     ✓ Live:TraderFront (交易前置, 可选)
  │     ✓ Live:BrokerId / UserId / Password / AuthCode / AppId
  │     ✓ Risk:MaxPositionPerInstrument / MaxDrawdownPct
  │
  ├─ 2. 构建 DI 容器
  │     FutureRegistry      ← symbols.json (75品种)
  │     CtpLiveFeed         ← IDataFeed, 行情接入
  │     ContractActivityTracker ← 60秒观察期, 筛选活跃合约
  │     RiskController      ← 风控横切层
  │     ExecutionHandler    ← IExecutionHandler, IsLive=true
  │     PortfolioManager    ← 资金+仓位
  │     IndicatorManager    ← 指标计算
  │     StrategyContainer   ← 策略注册
  │     BarStore            ← DuckDB 或 SQLite
  │     TickCsvWriter       ← 金数源格式 CSV 落盘
  │     (可选) CtpTraderBridge ← SendToExchange 委托注入
  │
  ├─ 3. 启动 SignalR Hub (REST API + 实时推送)
  │     /hubs/engine → WPF Terminal 连接
  │
  ├─ 4. CtpLiveFeed.Connect()
  │     → CTP MdApi 连接行情前置
  │     → 订阅全市场合约 Quote
  │     → Channel<TickRecord> 输出
  │
  ├─ 5. (可选) CtpTraderBridge.Connect()
  │     → CTP TraderApi 连接交易前置
  │     → Authenticate → Login
  │     → FillChannel 接收成交回报
  │
  └─ 6. TradingEngine.RunAsync(ct)
        → 主循环: await foreach (event in _dataFeed.StreamAsync(ct))
        → Tick 路径: Tick → indicators → strategies.OnTick → execution.ProcessTick
        → Bar 路径:  Bar  → indicators.Feed → strategies.OnBar → 下单 → 撮合
        → 持久化:    TickCsvWriter + BarStore
        → 推送:      SignalR → WPF Terminal
```

## 文件职责

| 文件 | 职责 |
|------|------|
| `CtpLiveFeed.cs` | `IDataFeed` 实现。CTP 行情接入 → Channel → `IAsyncEnumerable<EngineEvent>` |
| `CtpTraderBridge.cs` | CTP 交易桥接。`SendToExchange` → CTP InsertOrder，回报 → `FillChannel` |
| `ContractActivityTracker.cs` | 观察期内统计合约活跃度，筛选高流动性合约减少订阅量 |
| `PeriodMaintainer.cs` | 后台自动维护：每 5min 增量刷新 5min/15min/week 连续合约表 |
| `LiveDataCollector.cs` | 独立数据落盘：Tick CSV + 1min/day Bar，与引擎并行 |

## 数据存储

默认 DuckDB，`data/bars_live.duckdb`。配置 `appsettings.local.json`:

```json
"Live": {
    "UseDuckDB": "true",
    "Database": "bars_live.duckdb",
    "DataPath": "data"
}
```

多周期（5min/15min/week）和连续合约（xxx000）由 PeriodMaintainer 自动生成，盘中每 5 分钟增量更新。

## 与回测的差异

| 维度 | 回测 | 实盘 |
|------|------|------|
| DataFeed | `HistoricalBarFeed` (DuckDB) | `CtpLiveFeed` (CTP MdApi) |
| DB | `bars_history.duckdb` (只读) | `bars_live.duckdb` (读写) |
| 多周期 | 预计算表 | PeriodMaintainer 实时维护 |
| 撮合 | `ExecutionHandler.ProcessBar/ProcessTick` | `CtpTraderBridge.SendOrder` → CTP |
| 报告 | 生成 `EngineReport` → JSON | 不生成报告，SignalR 推送实时状态 |
| 风控 | `CheckPreOrder` (同) | `CheckPreOrder` (同) + CTP 自身风控 |
| 结束条件 | 数据流结束 | `CancellationToken` (手动停止) |

## 调度

| 时段 | 北京时间 | 动作 |
|------|---------|------|
| 日盘 | 08:30-15:30 | 自动连接 CTP，采集+交易 |
| 收盘 | 15:30 | Flush Bar，断开 CTP |
| 夜盘 | 20:30-03:00 | 自动连接 CTP，采集+交易 |
| 收盘 | 03:00 | Flush Bar，断开 CTP |
| 周末/节假日 | 全天 | 休市等待，到期自动恢复 |
