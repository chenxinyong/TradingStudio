# 回测配置指南

> Phase 2a/2b 回测引擎的完整使用说明。v0.2.0+

---

## 1. 命令行

```
TradingStudio.exe backtest --config <strategy.json> [选项]
```

| 参数 | 简写 | 必需 | 默认值 | 说明 |
|------|------|:---:|--------|------|
| `--config` | `-c` | ✅ | — | 策略 JSON 文件路径 |
| `--mode` | `-m` | — | `bar` | `bar` = SQLite 分钟回放, `tick` = CSV 毫秒回放 |
| `--db` | `-d` | — | `bars_history.db` | Bar 模式的 SQLite 数据库路径 |
| `--data-dir` | — | tick 必需 | — | Tick 模式的 CSV 文件目录 |
| `--start` | — | — | `2020-01-01` | 回测起始日期 (yyyy-MM-dd) |
| `--end` | — | — | `2030-01-01` | 回测结束日期 (yyyy-MM-dd) |
| `--symbols` | — | — | `symbols.json` | 品种规格文件路径 |

### 示例

```bash
# Bar 模式 — 最快，用于参数粗调
TradingStudio.exe backtest --config ma-cross-rb.json

# Bar 模式，指定时间范围和数据库
TradingStudio.exe backtest --config ma-cross-rb.json --db bars_history.db --start 2020-01-02 --end 2020-01-31

# Tick 模式 — 最精确，用于实盘前验证
TradingStudio.exe backtest --config ma-cross-rb.json --mode tick --data-dir C:\Data\tick_jan

# 多品种策略
TradingStudio.exe backtest --config spread.json --mode bar --db bars_history.db
```

---

## 2. 策略 JSON 配置

### 完整字段

```json
{
  "StrategyId":             "策略标识（报告 + 日志中使用）",
  "StrategyType":           "策略类名（去掉 Strategy 后缀），如 MaCross",
  "Description":            "策略描述（可选）",
  "Version":                1,

  "Instruments":            ["品种代码列表"],
  "PrimaryBarType":         "bars_1min | bars_day",

  "AllocatedCapital":       100000,
  "MaxDrawdownPct":         0.20,
  "MaxPositionPerInstrument": 5,
  "Priority":               1,

  "Parameters": {
    "参数名": "值"
  },

  "RiskRules": [],
  "SessionFilter":          "All | DayOnly | NightOnly",
  "SkipAuction":            true
}
```

### 字段说明

| 字段 | 类型 | 说明 |
|------|------|------|
| `StrategyId` | string | 唯一标识。报告、日志、分账均以此区分策略 |
| `StrategyType` | string | 策略类名去掉 `Strategy` 后缀。自动发现，无需手动注册 |
| `Instruments` | string[] | 品种代码。回测时加载这些品种的历史数据 |
| `PrimaryBarType` | string | `bars_1min` (1分钟 K 线) 或 `bars_day` (日线) |
| `AllocatedCapital` | decimal | 该策略的分配资金 |
| `MaxDrawdownPct` | decimal | 回撤上限，触发风控告警 |
| `MaxPositionPerInstrument` | int | 单品种最大持仓手数 |
| `Priority` | int | 多策略并发时，争抢流动性的优先级 (越大越优先) |
| `Parameters` | object | 策略参数。key 对应 `[StrategyParameter]` 属性名 |
| `RiskRules` | array | 风控规则列表 (Phase 2c) |
| `SessionFilter` | string | `All` = 全时段, `DayOnly` = 仅日盘, `NightOnly` = 仅夜盘 |
| `SkipAuction` | bool | 是否跳过集合竞价时段的 Bar |

### 示例

```json
{
  "StrategyId": "ma-cross-rb",
  "StrategyType": "MaCross",
  "Instruments": ["rb2005"],
  "PrimaryBarType": "bars_1min",
  "AllocatedCapital": 100000,
  "Parameters": {
    "FastPeriod": 5,
    "SlowPeriod": 20,
    "Quantity": 1
  }
}
```

---

## 3. 策略开发

### 实现接口

```csharp
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;

public class MyStrategy : IStrategy
{
    [StrategyParameter(Description = "快线周期", DefaultValue = 5, Min = 2, Max = 60, Category = "Entry")]
    public int FastPeriod { get; set; } = 5;

    public string Name => "我的策略";

    public void Initialize(StrategyContext context) { /* 注册指标、记录日志 */ }
    public void OnTick(TickRecord tick, string instrumentId) { /* Tick 回调 */ }
    public void OnBar(Bar bar) { /* Bar 回调 — 主决策入口 */ }
    public void OnOrderEvent(OrderEvent evt) { /* 成交通知 */ }
    public void OnAlert(MonitorAlert alert) { /* 告警回调 (可选) */ }
    public void OnEndOfAlgorithm() { /* 回测结束 */ }
}
```

### 参数声明约定

- 用 `[StrategyParameter]` 标记属性
- `Category`: `Entry` (入场), `Exit` (出场), `Risk` (风控), `Position` (仓位)
- `DefaultValue`: 代码默认值，JSON 可覆盖
- `Min` / `Max`: 参数扫描范围

### 自动注册

`StrategyFactory.DiscoverFromAssembly()` 自动扫描程序集中所有 `IStrategy` 实现。类名 `MaCrossStrategy` → JSON 中 `"StrategyType": "MaCross"`。

### 策略上下文 (StrategyContext)

策略与引擎的唯一触点：

```
行情查询:  GetBarHistory() / GetRecentBars() / GetTickHistory()
指标注册:  RegisterIndicator() / GetIndicatorValue() / GetIndicator()
下单:      MarketBuy() / MarketSell() / LimitBuy() / LimitSell()
           StopBuy() / StopSell() / ClosePosition() / CancelOrder()
仓位:      GetPosition() / Positions / Equity / AvailableCash
品种:      GetFuture() / SubscribedInstruments
日志:      Log() / LogWarning() / LogError()
```

### 日志示例

```csharp
_ctx.Log($"初始化: {Name} on {_instrument}");
_ctx.LogWarning($"价差异常: {tick.SpreadDouble:F2}");
_ctx.LogError($"拒绝: {evt.Message}");
```

输出:
```
[ma-cross-rb] 初始化: MA双均线突破 on rb2005
[ma-cross-rb] 成交: rb2005 Buy 1手 @ 3584.00
[ma-cross-rb] 回测结束. 最终权益: ¥1,037,260.96
```

---

## 4. 回测报告

### Console 输出

```
═══════════════════════════════════
  Backtest Report
═══════════════════════════════════
  Final Equity:    ¥1,037,300.96
  Total Return:         937.30%
  Max Drawdown:           0.16%
  ── ma-cross-rb ──
    Starting:       ¥100,000.00
    Final Equity:  ¥1,037,260.96
    Net Profit:     ¥937,260.96
    Trades:                 378
    Win Rate:             23.8%
    Avg Win:             ¥63.65
    Avg Loss:           ¥-47.99
    Total Fees:       ¥2,693.90
    Max Drawdown:         0.16%
  ── Monitor ──
    Total Slippage:      0.00
    Alerts:                  2
    Max ConsecLoss:      27
═══════════════════════════════════
  Report saved: ma-cross-rb.report.json
```

### JSON 报告

配置同名 `.report.json`。包含完整的交易列表、权益曲线、监控摘要。

---

## 5. Bar vs Tick 对比

| | Bar 模式 | Tick 模式 |
|---|---|---|
| 数据源 | SQLite bars_1min | CSV K-way merge |
| 时间精度 | 分钟 | 毫秒 |
| 市价成交价 | 下一根 Bar.Open | AskPrice1 + 1跳 |
| 滑点 | 0 | 真实 Bid/Ask 价差 |
| 速度 | 秒级 (~10万条/年) | 分钟级 (~5万条/天) |
| 适用场景 | 参数粗调、快速筛选 | 实盘前精确验证 |

---

## 6. 数据准备

### Bar 模式

需要 SQLite 数据库中有 `bars_1min` 或 `bars_day` 表。通过 ToolBox 导入：

```bash
TradingStudio.ToolBox import-jinshuyuan --layer active --from-month 202001 --to-month 202001 --db bars_history.db
```

### Tick 模式

需要将金数源 RAR 中的 CSV 提取到目录：

```bash
UnRAR.exe x FutAC_TickKZ_CTP_Daily_202001.rar -pwww.jinshuyuan.net "rb2005_*" ./tick_jan/
```

---

## 7. FeedbackMonitor 告警阈值

默认阈值（可通过 `FeedbackThresholds` 配置）：

| 告警 | 阈值 | 说明 |
|------|------|------|
| ConsecutiveLosses | 5 连败 | 策略可能失效 |
| HighSlippage | 平均滑点 > 3 | 流动性不足 |
| HighRejectRate | 拒绝率 > 10% | 风控或配置问题 |
| DrawdownWarning | 回撤 > 15% | 策略风控触发 |
| AbnormalFrequency | > 10笔/分钟 | 异常交易频率 |
