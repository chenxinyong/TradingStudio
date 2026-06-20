# TradingEngine — 主循环事件序列

## 整体架构

```
IDataFeed (HistoricalBarFeed / CtpLiveFeed / HistoricalTickFeed)
  │ IAsyncEnumerable<EngineEvent>
  │
  ▼
TradingEngine.RunAsync()
  │
  ├─ TickEvent  ──── Tick 路径 (高频)
  └─ BarEvent   ──── Bar 路径 (中低频)
```

## 初始化序列

```
1. StrategyFactory.Create(config)        ← JSON → IStrategy 实例
2. PortfolioManager.CreateSubPortfolio   ← 分账
3. HistoricalBarFeed.LoadBars            ← 预热数据加载 (WarmupDays 天)
4. EngineStrategyContext                 ← 包装上下文
5. strategy.Initialize(ctx)              ← 策略初始化 (加载指标、缠论分析)
6. ctx.IsWarmup = true;
   foreach(bar in warmupHistory)
     strategy.OnBar(bar)                 ← 预热: 策略只更新状态, 不产生信号
   ctx.IsWarmup = false
7. StrategyContainer.Register            ← 注册 + 策略优先级
```

## Bar 路径（主力路径，回测默认）

```
BarEvent arrives (每根闭合 Bar, 所有品种已时间对齐)
  │
  ├─ ① TickSnapshot.Update             ← 更新行情快照
  │
  ├─ ② PortfolioManager.UpdateMarketPrice(bar)  ← 更新未实现盈亏 → 重算 Equity
  │
  ├─ ③ IndicatorManager.Feed(bar)       ← 所有注册指标 Update (SMA/BOLL/MACD/RSI)
  │
  ├─ ④ StrategyContainer.DispatchBar(bar)
  │     └─ strategy.OnBar(bar)
  │         ├─ ctx.GetIndicatorValue     ← 查询指标 → 信号逻辑
  │         ├─ ctx.MarketBuy/Sell       ← 下单 → ExecutionHandler.Submit
  │         └─ ctx.ClosePosition        ← 平仓 → ExecutionHandler.Submit
  │
  ├─ ⑤ ExecutionHandler.ProcessBar(bar)  ← 撮合 ActiveOrders
  │     └─ 遍历挂单 → MatchBar (市价/限价/止损, 涨跌停检测)
  │         └─ fill → PortfolioManager.ProcessFill → Trade 记录
  │
  ├─ ⑥ StrategyContainer.DispatchOrderEvent(fill) ← 通知策略成交/拒单
  │
  ├─ ⑦ FeedbackMonitor.SamplePortfolio  ← 采样权益 + 告警检测
  │
  ├─ ⑧ equityCurve.Add(bar.Time, Equity) ← 权益采样
  │
  └─ ⑨ PortfolioManager.ForceCloseNearDelivery ← 交割月检查
```

## Tick 路径（高频模式，--mode tick）

```
Tick arrives (每个 Tick, 单品种)
  │
  ├─ ① TickSnapshot.Update
  │
  ├─ ② ExecutionHandler.ProcessTick     ← 先撮合已有挂单
  │     └─ 增量成交量 + Bid/Ask 价格 + 流动性约束
  │
  ├─ ③ StrategyContainer.DispatchTick   ← strategy.OnTick
  │     └─ 策略可能下单 (市价单)
  │
  ├─ ④ (TickFillDelay==0) ExecutionHandler.ProcessTick ← 立即撮合新下的市价单
  │
  ├─ ⑤ BarAggregator 收到 Tick → 可能闭合 1min Bar
  │     └─ BarEvent 发射 → Bar 路径中的步骤
  │
  └─ ⑥ TickCsvWriter.Write              ← CSV 落盘 (实盘模式)
```

## 引擎报告生成（回测结束）

```
RunAsync 正常结束 (数据流耗尽)
  │
  ├─ ① feedback.Flush → FeedbackMonitor.GetSummary
  ├─ ② stats = TradeStatistics.From(trades, equityCurve)
  ├─ ③ report = PerformanceReport.Generate(strategyId, subPortfolio, trades, equityCurve)
  │       ├── DrawdownCalculator.CalculateMaxDrawdown(equityCurve)
  │       └── CalculateRatios(equityCurve) → Sharpe, Sortino
  ├─ ④ TradeStatistics.From(trades) → 胜率/盈亏比/连胜连败/月度分布
  └─ ⑤ EngineReport → JSON 序列化输出
```

## 实盘模式差异

| | 回测 | 实盘 |
|---|---|---|
| 数据流 | DataFeed 有限, 自然结束 | Ctrl+C 手动停止 |
| 撮合 | 本地撮合 (Bar/Tick) | 市价单 → CTP, 限价/止损本地 |
| 报告 | 完整 EngineReport | 不生成, SignalR 推送实时状态 |
| 预热 | WarmupDays 加载历史 | 启动后才开始积累 |
