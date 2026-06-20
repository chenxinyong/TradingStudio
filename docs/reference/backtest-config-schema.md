# 回测配置 JSON Schema

## 完整字段

```json
{
  // ═══ 标识 ═══
  "StrategyId": "MaCross-RB",       // 策略实例唯一 ID, 用于报告区分
  "StrategyType": "MaCross",        // 策略类型名 = 类名去掉 "Strategy" 后缀
  "Description": "RB 15min 均线交叉", // 可选描述
  "Version": 1,                      // 配置版本号

  // ═══ 品种订阅 ═══
  "Instruments": ["rb"],             // 品种代码列表 (产品级, 如 "rb" 非 "rb2608")
  "PrimaryBarType": "bars_1min",    // 源表名: bars_1min (1min) 或 bars_day (日线)
  "BarPeriodMinutes": 15,           // Bar 周期: 1/5/15/30/60 (仅回测, 1min→Nmin 聚合)

  // ═══ 资金分配 ═══
  "AllocatedCapital": 1000000,       // 分配给此策略的初始资金 (元)
  "MaxDrawdownPct": 0.30,           // 最大回撤比例 (0.30=30%, 触发后停止策略)
  "MaxPositionPerInstrument": 5,    // 单品种最大持仓手数

  // ═══ 执行 ═══
  "Priority": 1,                     // 同品种多策略争抢流动性时优先级 (越小越优先)
  "SessionFilter": "All",           // 交易时段: All / DayOnly / NightOnly
  "SkipAuction": true,              // 跳过集合竞价 (建议 true)

  // ═══ 策略参数 (动态字段, 由策略的 [StrategyParameter] 属性定义) ═══
  "Parameters": {
    "FastPeriod": 5,
    "SlowPeriod": 20,
    "AtrPeriod": 14,
    "RiskPerTrade": 0.02
  },

  // ═══ 风控规则 (可选覆盖全局设置) ═══
  "RiskRules": []
}
```

## StrategyType → 策略类映射

| StrategyType | 策略类 | 所在项目 |
|-------------|--------|----------|
| `MaCross` | `MaCrossStrategy` | Engine/Examples |
| `MaCrossMultiTf` | `MaCrossMultiTfStrategy` | Engine/Examples |
| `DonchianTrend` | `DonchianTrendStrategy` | Strategy |
| `SmaMacd` | `SmaMacdStrategy` | Strategy |
| `ChanLun` | `ChanLunStrategy` | Strategy |

规则: `StrategyFactory.DiscoverFromAssembly` 扫描程序集 → 类名去掉 "Strategy" 后缀 → 注册。也可以用 `StrategyFactory.Register<T>("CustomName")` 手动注册。

## Parameters 验证

策略类通过 `[StrategyParameter]` 声明可配置参数:

```csharp
[StrategyParameter(Description = "快线周期", DefaultValue = 5, Min = 2, Max = 60)]
public int FastPeriod { get; set; } = 5;
```

StrategyFactory.Create 时:
1. 从 JSON Parameters 读取值 → 覆盖默认值
2. 验证 Min ≤ value ≤ Max
3. Required=true 时必须提供
4. Category 仅用于 UI 分组

## BarPeriodMinutes 工作方式

```
1  → bars_1min 直接输出
5  → MultiBarAggregator(5):  bars_1min → 5min Bar
15 → MultiBarAggregator(15): bars_1min → 15min Bar
30 → MultiBarAggregator(30): bars_1min → 30min Bar
60 → MultiBarAggregator(60): bars_1min → 60min Bar
```

聚合规则: OHLC = 首开/最高/最低/尾收, Volume = 求和。

## SessionFilter

| 值 | 含义 |
|----|------|
| `All` | 所有时段 (默认) |
| `DayOnly` | 仅日盘 (09:00-15:00) |
| `NightOnly` | 仅夜盘 (21:00-02:30) |
