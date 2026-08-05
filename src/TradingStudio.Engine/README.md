# PortfolioManager — 资金与仓位模型

## 核心公式

```
Equity = Cash + MarginUsed + Σ(UnrealizedPnl)       ← 总权益
Cash   = 初始资金 - 已扣保证金 - 已扣手续费 + 已实现盈亏  ← 可用现金
MarginUsed = Σ(每个持仓的保证金)                       ← 已占用保证金

平仓盈亏 = (卖出价 - 买入价) × 数量 × 合约乘数 × 方向
           - 开仓手续费 - 平仓手续费
           + 释放的保证金
```

## 记账时机

| 事件 | 现金变化 | 保证金变化 | 权益变化 |
|------|----------|------------|----------|
| **开仓** | Cash -= Fee + Margin | MarginUsed += Margin | 不变（资产形式转换） |
| **平仓** | Cash += PnL - Fee + Margin(释放) | MarginUsed -= Margin | Equity = Cash + MarginUsed + 未实现盈亏 |
| **加仓** | Cash -= Fee + ΔMargin | MarginUsed += ΔMargin | 不变 |
| **Bar 收盘** | Cash 不变 | MarginUsed 不变 | 更新 UnrealizedPnl → 重算 Equity |
| **交割强平** | 按收盘价平仓，无手续费 | 释放全部保证金 | 同上 |

## 多策略分账 (SubPortfolio)

```
PortfolioManager (总账)
  ├── Cash (总现金池, 共享)
  ├── MarginUsed (总保证金)
  ├── Positions (总持仓, key=instrumentId)
  │
  └── SubPortfolio "MaCross-RB"  ← CreateSubPortfolio(strategyId, allocatedCapital)
  │     ├── AllocatedCapital: 50万
  │     ├── Cash: 策略自身现金
  │     ├── MarginUsed: 策略自身保证金
  │     └── Equity: 策略自身权益
  │
  └── SubPortfolio "ChanLun-SA"
        ├── AllocatedCapital: 50万
        └── ...
```

分账不是物理隔离（现金池共享），是逻辑跟踪。子账户用于：
- 绩效报告按策略独立计算
- 权益曲线按策略独立采样
- 回撤按策略独立监控

## 关键约束

1. **保证金必须从现金扣除** — 开仓时 `Cash -= Fee + Margin`，否则 `Equity = Cash + Margin` 双重计算
2. **平仓释放保证金** — 平仓时 `Cash += PnL - Fee + Margin`，`MarginUsed -= Margin`
3. **锁内记账** — 实盘中 FillChannel 线程和主循环线程并发，所有 `ProcessFill` 加锁
4. **交割月强平** — 到期前 2 月自动平仓，模拟真实规则（连续合约 v000 跳过）
5. **平今手续费** — 当天开当天平使用 `CloseTodayFeeRate`（平今免/平今加倍）

## 线程模型 (v3: OrderEventPump)

```
主循环线程:              FillChannel 消费线程:        OrderEventPump 线程:
  ProcessBar/ProcessTick   CTP 成交回报 →              OrderOutbox 唯一消费者 →
    → UpdateMarketPrice      → ProcessFill               → PushService.Sink (SignalR)
    → ForceCloseNearDelivery   → outboxWriter.TryWrite     → Persistence.Sink (DuckDB)
    (lock _sync)               (lock _sync)                 (串行, 无竞态)
```

- FillChannel: 单一消费者 (TradingEngine), 无竞态
- OrderOutbox → OrderEventPump: 单消费者泵, 串行分发 IOrderEventSink (借鉴 StockSharp CTP)
- 可重入锁：`Equity` getter 内部有锁, 在 `ProcessFillLocked` 中持同一锁时不会死锁
