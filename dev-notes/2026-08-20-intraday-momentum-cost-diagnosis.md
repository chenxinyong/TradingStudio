# IntradayMom 策略成本诊断 — 2026-08-20

> 状态：诊断完成，待审计滑点累计逻辑
> 关联：[[因子研究/有效因子深度分析]]、CLAUDE.md 第三阶段「策略研发」

---

## 背景问题

因子研究笔记记录「IntradayMom 执行质量敏感：Python Sharpe 1.16 vs C# 含成本 ≈ 0」。
本次诊断目标是搞清楚「含成本 ≈ 0」的真实原因，并做成本归因。

## 三个真相（层层递进）

### 1. Python「Sharpe 1.16」是假回测

`scripts/factor_research/intraday_momentum_strategy.py` 的回测用**布朗桥模拟日内路径**，不是真实分钟数据：

- 脚本第 30 行自述「历史回测使用日内路径模拟（布朗桥 + 日线 OHLC 约束）」
- `simulate_intraday_path()` 生成 Open→Close 的随机路径，取第 30 步作「30 分钟信号」
- 信号 p_30 是**随机生成**的，出场用**真实收盘价**，二者无真实预测关系

**结论：Sharpe 1.16 不可作为决策依据。**

### 2. CrossSectionalIntradayMomStrategy 日线版因子是死的

配置 `configs/cross_sectional_im_day.json` 用 `bars_day`（日线）。

`src/TradingStudio.Core/Factors/MicrostructureFactors.cs` 的 `IntradayMomFactor.Update`：

```csharp
if (!_dayMomComputed && _barsInDay * barPeriodMinutes >= _warmupMinutes)  // _warmupMinutes=30
    _close30Min = price;
```

日线每天 1 根 bar，`1 × 5(默认BarPeriodMinutes) = 5 < 30` → `_close30Min` 永远 NaN → 因子恒 `IsReady=false` → `RebalancePortfolio` rankings 恒空 → **策略基本 0 交易**。

**「C# 含成本 ≈ 0」的主因不是成本，而是没开仓。**

### 3. 真实含成本回测是 -99%，但归因清晰

`archive/configs/intraday_composite_15min.report.json`（`Composite-ICW-XS-15min`，15min 真实数据，09:30 限价入场 / 14:45 市价平仓）：

| 项目 | 金额 | 占起始资金 |
|------|------|-----------|
| 毛利润（不含成本） | +212 万 | +106% |
| 手续费 | -99 万 | -50% |
| 滑点 | -311 万 | -156% |
| 净结果 | -198 万 | -99% |

**核心结论：毛 alpha 是正的（+212 万），滑点是头号杀手（占成本 76%）。**

## ✅ 根因已确认（审计完成 2026-08-21）

`totalSlippage=311 万` **不是真实滑点，是计量 bug**。定位到 `ExecutionHandler.MatchBar` 第 567-569 行：

```csharp
var slipPerLot = Math.Abs(fillPrice - (decimal)bar.OpenDouble);
var slippage = slipPerLot * fillQty * future.TradingUnit;
```

这行对**所有订单类型**（市价/限价/止损）统一按 `|成交价 − Open|` 计滑点，但三者的偏差方向完全不同：

| 订单类型 | 成交价规则 | 偏差方向 | 正确滑点 |
|---------|-----------|---------|---------|
| 市价单 | Open ± 1 tick | 不利（跨价差） | `1 tick × qty × unit` ✅ 本来就没问题 |
| **限价单** | min/max(limitPrice, Open) | **有利**（买在 Open 下/卖在 Open 上） | **应为 0**，却被 Math.Abs 记成正成本 ❌ |
| 止损单 | max/min(stopPrice, Open) | 不利（穿价） | 才算真滑点 |

而 `IntradaySignalExecutor` 的**入场恰是限价单**（第 153-157 行 `LimitBuy/LimitSell @ 09:30 价格`，注释自述「零滑点入场」）。这些限价单成交价 ≤ Open（买）/ ≥ Open（卖），本不该有任何滑点，却被 Math.Abs 把「有利偏差」记成成本——**311 万滑点绝大部分来自入场限价单的虚假滑点**。

`alertCount=351 万` 是同一根因的次生效应：`FeedbackMonitor.CheckAlerts` 每根 bar 调用 `_totalAlertCount += _alerts.Count`，因滑点虚高，`HighSlippage` 告警几乎每 bar 触发 → 百万级累积。

### 附带发现：口径不一致（真实但非主因）

- bar 模式 `MatchBar`：`Slippage` = 总成本元（`slipPerLot × fillQty × TradingUnit`）
- tick 模式 `TryMatchTick`：`Slippage` = 每单位价格偏差（`|fillPrice − midPrice|`，第 387 行，未乘手数/单位）

两口径混用同一字段，但本项目回测走 bar 模式，tick 口径不影响本次报告。

## 下一步（按优先级）

1. **修滑点计量**：`MatchBar` 中限价单 slippage 置 0；止损单用 `|fillPrice − stopPrice| × qty × unit`；市价单保留 `1 tick × qty × unit`。修完后重跑 `intraday_composite_15min` 看真实含成本收益。
2. **修周期错配**：删掉或日内化 `CrossSectionalIntradayMomStrategy` 的日线配置。
3. **（修完计量后再评估）降滑点**：平仓市价单→限价单、按 tick×unit/价格过滤、降频率收窄 TopN —— 这些只有在滑点计量正确后才值得做，否则是拿着错误数字调参。

## 关键文件

- 因子定义：`src/TradingStudio.Core/Factors/MicrostructureFactors.cs`
- 日线版策略：`src/TradingStudio.Engine/Examples/CrossSectionalIntradayMomStrategy.cs`
- 日内执行器：`src/TradingStudio.Engine/Examples/IntradaySignalExecutor.cs`
- 撮合/滑点：`src/TradingStudio.Engine/ExecutionHandler.cs`（`MatchBar` / `MarketSlippage`）
- 成本累计：`src/TradingStudio.Engine/FeedbackMonitor.cs`（`RecordFill`）
- 回测入口：`src/TradingStudio/Commands/BacktestCommand.cs`
