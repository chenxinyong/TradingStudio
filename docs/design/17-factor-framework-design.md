# TradingStudio 因子框架设计

> 2026-07-07 | Ernie Chan《量化交易从入门到精通》学习实践
>
> 从 "指标→bool信号" 升级到 "因子→标准化→合成→信号强度→仓位映射"
>
> **参考:** Ernie Chan (Quantitative Trading, Algorithmic Trading), Wesley Gray (Quantitative Momentum)
> **学习路线:** `docs/learning/factor-learning-roadmap.md` / `docs/learning/quant-trading-fast-track.md`

---

## 目录

1. [动机：为什么需要因子层](#1-动机为什么需要因子层)
2. [架构总览：因子在 TradingStudio 中的位置](#2-架构总览因子在-tradingstudio-中的位置)
3. [因子接口与基类](#3-因子接口与基类)
4. [具体因子实现](#4-具体因子实现)
5. [因子合成引擎](#5-因子合成引擎)
6. [因子评估工具](#6-因子评估工具)
7. [风险与仓位管理](#7-风险与仓位管理)
8. [回测分析工具](#8-回测分析工具)
9. [Python 研究管线](#9-python-研究管线)
10. [与现有引擎的集成](#10-与现有引擎的集成)
11. [文件索引](#11-文件索引)

---

## 1. 动机：为什么需要因子层

### 1.1 当前架构的问题

```
Bar → SMA(233) → 价格穿越? → 入场/出场  (bool 信号)
```

| 问题 | 影响 |
|------|------|
| 信号是 bool | 无法区分 "勉强穿越" 和 "强力突破" |
| 仓位固定 | 信号强弱不反映在仓位大小上 |
| 单一指标决策 | SMA233 失效时策略完全崩溃 |
| 参数经验主义 | SMA=233 为什么不是 230 或 236？ |
| 无法评估指标质量 | 只能看最终 Sharpe，不知道 SMA 这个 "因子" 本身有没有预测力 |

### 1.2 目标架构

```
Bar → 动量因子(连续值) → z-score/百分位 →
      趋势因子(连续值) → z-score/百分位 → 因子合成 → 信号强度[-1,+1] → 仓位映射
      波动率因子(连续值) → z-score/百分位 →
```

**核心升级：**

| 维度 | 旧 | 新 |
|------|----|----|
| 信号类型 | `bool` (有/无) | `double` (-1 到 +1，连续强度) |
| 决策依据 | 单一指标 | 多因子合成 (等权/IC 加权/一致性投票) |
| 仓位管理 | 固定比例 | `信号强度 × 凯利最优仓位 × 风险预算` |
| 因子评估 | 看回测 Sharpe | IC 分析 + 分位数收益 + 衰减曲线 |
| 参数选择 | 经验值 | 参数平原扫描 + 稳定性检验 |

### 1.3 三个来源

```
Ernie Chan (Ch1-7)          Wesley Gray (Ch4-7)         Andreas Clenow (Ch4-7)
├─ 回测方法论                 ├─ 因子 Universe 筛选        ├─ 波动率目标仓位
├─ ADF 平稳性                ├─ 动量度量 (3 种变体)       ├─ 风险平价
├─ 时间序列动量               ├─ 因子合成 (等权/IC)        ├─ 策略组合
├─ 凯利公式                   └─ 组合构建 (Top-N + 加权)   └─ 退役标准
└─ 协整/季节性
```

---

## 2. 架构总览：因子在 TradingStudio 中的位置

### 2.1 分层架构

```
┌──────────────────────────────────────────────────────────────┐
│                    Strategy Layer                            │
│  FactorStrategy.cs  ← 使用因子框架的策略                     │
│  SmaMacdStrategy.cs ← 旧架构策略 (逐步迁移)                  │
└────────────────────────┬─────────────────────────────────────┘
                         │ 依赖
┌────────────────────────▼─────────────────────────────────────┐
│                    Factor Layer  ← NEW                       │
│  ┌─────────────┐  ┌──────────────────┐  ┌────────────────┐  │
│  │ IFactor     │  │ FactorComposite  │  │ FactorEvaluator│  │
│  │ FactorBase  │  │ (等权/IC/一致性)  │  │ (IC/分位数)    │  │
│  │ TrendFactors│  │                  │  │                │  │
│  └─────────────┘  └──────────────────┘  └────────────────┘  │
└────────────────────────┬─────────────────────────────────────┘
                         │ 依赖
┌────────────────────────▼─────────────────────────────────────┐
│                    Analysis Layer  ← NEW                     │
│  ┌──────────────────┐  ┌──────────────────────────────┐     │
│  │ WalkForwardVal   │  │ ParameterStabilityAnalyzer   │     │
│  │ StrategyScorer   │  │ (单参数 + 多参数交互)         │     │
│  └──────────────────┘  └──────────────────────────────┘     │
└────────────────────────┬─────────────────────────────────────┘
                         │ 依赖
┌────────────────────────▼─────────────────────────────────────┐
│                    Risk Layer  ← NEW                         │
│  ┌──────────────────┐  ┌──────────────────┐                 │
│  │ KellyPositionSizer│  │ DailyRiskTracker │                 │
│  │ (凯利/分数凯利)    │  │ (三层风险断路器)  │                 │
│  └──────────────────┘  └──────────────────┘                 │
└────────────────────────┬─────────────────────────────────────┘
                         │ 依赖
┌────────────────────────▼─────────────────────────────────────┐
│                  Existing Engine Layer                       │
│  IStrategy / StrategyContext / TradingEngine / IDataFeed    │
│  ExecutionHandler / PortfolioManager / RiskController       │
│  IndicatorManager / PerformanceReport                       │
└──────────────────────────────────────────────────────────────┘
```

### 2.2 完整信号链路

```
Bar 数据 ──→ [TrendFactors: 动量/偏离/波动率/量比]
                  │  每个因子独立计算 → RawValue → ZScore → Percentile → SignalStrength
                  ↓
            FactorComposite.Evaluate()
                  │  等权 / IC 加权 / 一致性投票
                  ↓
            CompositeResult
              ├─ CompositeSignal [-1, +1]
              ├─ Confidence [0, 1]
              └─ Contributions (各因子明细)
                  │
                  ↓
            ToPositionRatio(maxRatio)
                  │  position = signal × confidence × maxRatio
                  ↓
            KellyPositionSizer.CalculateLots()
                  │  lots = (equity × positionRatio) / (price × multiplier × marginRate)
                  ↓
            DailyRiskTracker.RecordTrade()
                  │  日内/月/最大回撤 → 断路器
                  ↓
            下单 (ctx.MarketBuy / ctx.MarketSell)
```

---

## 3. 因子接口与基类

### 3.1 IFactor — 因子接口

```csharp
public interface IFactor
{
    string Name { get; }          // 唯一标识，如 "Mom20"
    string Category { get; }      // 类别: Momentum, Trend, Volatility, Value, Sentiment
    bool IsReady { get; }         // 预热完成？
    FactorValue LastValue { get; } // 最近一次因子值

    void Initialize(IReadOnlyList<double> warmupPrices);
    FactorValue Update(double price, double high, double low, double volume);
    void Reset();
}
```

**设计决策：**
- `Update()` 接受 price/high/low/volume 四个字段，覆盖所有常见因子类型
- 不需要的字段传 NaN，因子内部忽略
- `Initialize()` 用历史数据预热，策略启动时调用

### 3.2 FactorValue — 标准化输出

```csharp
public readonly struct FactorValue
{
    double RawValue;        // 原始值 (如 2.3% 收益率)
    double ZScore;          // z-score (偏离滚动均值的标准差数)
    double Percentile;      // 百分位 [0,1] (在滚动窗口中的排名)
    double SignalStrength;  // 信号强度 [-1,+1] (从 Percentile 映射)
    bool IsValid;           // 是否有效
}
```

**三种标准化的使用场景：**

| 输出 | 用途 | 示例 |
|------|------|------|
| `RawValue` | 调试、日志 | "Momentum(20) = +2.3%" |
| `ZScore` | 跨品种可比、阈值判断 | "动量超过 +2σ → 极端信号" |
| `Percentile` | 横截面排名、选品种 | "在所有品种中排 Top 10%" |
| `SignalStrength` | 仓位权重、下单决策 | "+0.6 → 做多 60% 仓位" |

### 3.3 FactorBase — 基类

自动处理标准化逻辑，子类只需实现 `Compute()`：

```
子类实现:
  Compute(price, high, low, volume) → rawValue

基类自动:
  rawValue → 滚动窗口(WindowSize) → ZScore + Percentile → SignalStrength
```

**关键参数：**

| 参数 | 默认值 | 含义 |
|------|:------:|------|
| `WindowSize` | 252 | 滚动标准化窗口 (≈1年日线) |
| `MinWarmupBars` | WindowSize/4 | 最少预热 Bar 数 |
| `ZScoreCap` | ±4.0 | z-score 截断（防异常值污染） |
| `LongThreshold` | 0.80 | 百分位 > 80% → 做多信号 |
| `ShortThreshold` | 0.20 | 百分位 < 20% → 做空信号 |

**信号映射逻辑：**

```
Percentile >= 0.80 → Signal = (pct - 0.80) / 0.20  →  (0, +1]
Percentile <= 0.20 → Signal = -(1.0 - pct/0.20)    →  [-1, 0)
中间 60%            → Signal = 0                      →  中性（不交易）
```

这是 Chan 的方法——只交易因子最明确的 40% 情况，中间 60% 视为噪声。

---

## 4. 具体因子实现

### 4.1 因子列表

| 因子 | 类名 | 类别 | 公式 | Lookback |
|------|------|------|------|:--------:|
| 时间序列动量 | `TimeSeriesMomentum` | Momentum | `(P_t - P_{t-N}) / P_{t-N}` | 20 |
| SMA 偏离度 | `SmaDeviation` | Trend | `(P_t - SMA_N) / SMA_N` | 20 |
| 历史波动率 | `HistoricalVolatility` | Volatility | `StdDev(returns, N) × √252` | 20 |
| 成交量比率 | `VolumeRatio` | Sentiment | `Volume_t / SMA(Volume, N)` | 20 |

所有因子均继承 `FactorBase`，自动获得滚动窗口标准化能力。

### 4.2 TimeSeriesMomentum — Chan Ch7.1

```
Momentum(t) = (Price(t) - Price(t - Lookback)) / Price(t - Lookback)

直观含义: 过去 20 根 Bar 涨了多少
标准化后: +1.5σ → "比过去一年 93% 的时间动量更强" → 做多信号
```

### 4.3 SmaDeviation

```
Deviation(t) = (Price(t) - SMA(t, Period)) / SMA(t, Period)

直观含义: 价格离均线多远
正值 = 价格在均线之上，趋势偏多
负值 = 价格在均线之下，趋势偏空
```

### 4.4 HistoricalVolatility

```
HV(t) = StdDev(daily_returns, period) × √252

直观含义: 年化波动率
高波动 = 风险大但机会也可能大
低波动 = 横盘等待突破
```

### 4.5 VolumeRatio

```
VolRatio(t) = Volume(t) / SMA(Volume, period)

> 1.0 = 放量（市场关注度高）
< 1.0 = 缩量（冷清）
```

### 4.6 因子扩展指南

添加新因子的步骤：
1. 继承 `FactorBase`
2. 在构造函数中设置 `FactorConfig`（Name, Category, WindowSize）
3. 实现 `Compute()` 方法
4. 可选：重写 `Reset()` 清理内部状态

---

## 5. 因子合成引擎

### 5.1 FactorComposite — Gray Ch6 方法

将多个 IFactor 合成为一个信号。

**四种合成模式：**

| 模式 | 算法 | 适用场景 |
|------|------|---------|
| **Equal** | 等权平均 | 最朴素，最不容易过拟合 → **默认推荐** |
| **VolInv** | 波动率倒数加权 | 降低极端信号因子的权重 |
| **ICAgg** | IC 加权 | 历史预测力强的因子权重更高（需要回测提供 IC） |
| **Agreement** | 一致性投票 | 保守：仅当 N/M 因子方向一致时产生信号 |

### 5.2 Equal 模式（默认）

```
CompositeSignal = Σ(Signal_i) / N

简单但有效。Gray: "等权往往是过拟合最少的选择。"
```

### 5.3 Agreement 模式（保守）

```
仅当 ≥ N×AgreementThreshold 个因子方向一致时 → 发出信号
否则 → 信号 = 0

AgreementThreshold 默认 0.67 (2/3)
```

### 5.4 CompositeResult

```csharp
public class CompositeResult
{
    double CompositeSignal;    // 合成信号 [-1, +1]
    double Confidence;         // 置信度 = |signal| × 因子一致度
    double AgreementRatio;     // 方向一致的比例
    List<FactorContribution> Contributions; // 各因子贡献明细
    string Verdict;            // 文字结论
}
```

**仓位映射：**
```csharp
double positionRatio = result.ToPositionRatio(maxRatio: 0.20);
// = CompositeSignal × Confidence × maxRatio
// 最终比例被截断在 [-maxRatio, +maxRatio]
```

---

## 6. 因子评估工具

### 6.1 FactorEvaluator — IC 分析 + 分位数收益

两个核心方法：

```
AnalyzeIC(snapshots)      → IcAnalysisResult   (因子有预测力吗？)
AnalyzeQuantiles(snapshots) → QuantileAnalysisResult (预测力单调吗？)
```

### 6.2 IC 分析

**Rank IC = Spearman 相关系数 (因子百分位_t, 下一期收益率_{t+1})**

```
输入: FactorSnapshot[]  — 每期包含 {FactorValues, ForwardReturns}
输出: IC 序列 → mean IC, IC IR, IC 衰减, 累积 IC 曲线
```

**Gray 的 IC 质量基准：**

| IC IR | 评级 | 含义 |
|:------|:----:|------|
| ≥ 0.50 | Excellent | 优秀因子，预测力强且稳定 |
| ≥ 0.30 | Good | 可用因子 |
| ≥ 0.10 | Weak | 弱预测力 |
| < 0.10 | Insufficient | 无效因子 |

**IC 衰减：** Lag 1/2/3/5/10/20 期的 IC，看预测力持续多久。
**IC 半衰期：** IC 衰减到 MeanIC/2 的 lag 数。越长越好。

### 6.3 分位数收益分析

按因子值将品种分成 5 组，计算每组平均未来收益：

```
Q1 (最弱 20%): avg_return = -0.02%/day
Q2:            avg_return = -0.01%/day
Q3:            avg_return =  0.00%/day
Q4:            avg_return = +0.01%/day
Q5 (最强 20%): avg_return = +0.03%/day

Q5 - Q1 = +0.05%/day ≈ +12.6%/year ← 因子多空收益
```

**优质因子标准：** 分位数收益单调递增 + Q5-Q1 年化 > 5%

### 6.4 FactorSnapshot — 评估用数据结构

```csharp
public class FactorSnapshot
{
    DateTime Timestamp;
    Dictionary<string, double> FactorValues;    // 品种 → 因子值
    Dictionary<string, double> ForwardReturns;  // 品种 → 下一期收益率
}
```

从回测引擎的 Bar 流中提取：每期计算因子值 + 记录下一期实际收益 → IC 分析。

---

## 7. 风险与仓位管理

### 7.1 KellyPositionSizer — Chan Ch6.1

**凯利公式 (连续复利版):**
```
f* = (μ - r) / σ²
```

**Chan 的实操建议：** 用分数凯利，永远不要用满。

| 参数 | 默认值 | 含义 |
|------|:------:|------|
| `KellyFraction` | 0.50 | 半凯利（推荐），1/4 凯利 = 0.25（保守） |
| `MaxPositionRatio` | 0.20 | 硬上限——即使凯利说 50%，也只用到 20% |
| `RiskPerTradeRatio` | 0.10 | 单笔风险占推荐仓位的比例 |

**两种输入模式：**
1. `CalculateFromReturns(returns)` — 从收益率序列估计 μ 和 σ
2. `CalculateFromWinLoss(winRate, plr)` — 从胜率和盈亏比快速估算

### 7.2 DailyRiskTracker — Chan Ch6.2

四层风险限制：

| 层级 | 限制 | 动作 |
|------|:----:|------|
| 单笔 | ≤ 权益的 2% | `KellyPositionSizer` 控制 |
| 日风险 | ≤ 权益的 5% | 日内触及 → 停止当日交易 |
| 月风险 | ≤ 权益的 15% | 当月触及 → 暂停当月交易 |
| 最大回撤 | ≤ 权益的 25% | 断路器 → 停止策略，人工恢复 |

**附加功能：**
- 连续亏损天数追踪（≥5 天 → 暂停）
- 滚动 Sharpe 监控（< 0.3 → 告警）
- `CanTrade()` 检查当前是否允许交易
- 日终 `CloseDay()` 记录日盈亏

---

## 8. 回测分析工具

### 8.1 WalkForwardValidator — Chan Ch3.5

**步进验证：** 滚动窗口 IS(训练) → OOS(测试) → 滑动 → 重复

```
窗口1: IS=[2020, 2022] OOS=[2023, 2023]
窗口2: IS=[2020.5, 2022.5] OOS=[2023.5, 2023.5]  ← 滑动半年
窗口3: ...
```

**核心输出：** `StabilityRatio = OOS Sharpe / IS Sharpe`

| 比率 | 评级 |
|:-----|:----:|
| > 0.90 | 优秀 |
| > 0.70 | 可接受 |
| < 0.50 | 过拟合 |

### 8.2 ParameterStabilityAnalyzer — Chan Ch3.7

**单参数分析：** 参数 ±20% 扰动后 Sharpe 的稳定性。

| 指标 | 含义 |
|------|------|
| 全局 CV | Sharpe 在整个扫描范围的变异系数 |
| 局部 CV | 最优参数 ±20% 范围的变异系数 |
| 高原宽度比 | 高 Sharpe 区域占参数总范围的比例 |

**多参数交互分析：** Top 10% 参数组合在两个维度上的覆盖比例。

### 8.3 StrategyScorer — Chan Ch3.4

**多维度评分 (0-100)：** 不只 Sharpe。

| 维度 | 权重 | Chan 的理由 |
|------|:----:|-----------|
| Sharpe | 1.5 | 核心指标 |
| Sortino | 1.0 | 下行风险更重要 |
| Drawdown | 1.2 | 和 Sharpe 同等重要 |
| Stability (OOS/IS) | 1.5 | **最重要的指标** |
| PLR (盈亏比) | 0.8 | 比胜率重要 |
| WinRate | 0.3 | 影响心理，不核心 |

---

## 9. Python 研究管线

### 9.1 脚本清单

| 脚本 | 目录 | 对应章节 | 回答的问题 |
|------|------|:--------:|-----------|
| `adf_mean_reversion.py` | `scripts/factor_research/` | Chan Ch7.3 | 哪些品种价格平稳？适合均值回归吗？ |
| `cointegration_pairs.py` | `scripts/factor_research/` | Chan Ch7.3 | 同产业链品种对是否协整？能做配对交易吗？ |
| `seasonality_analyzer.py` | `scripts/factor_research/` | Chan Ch7.6 | 哪些品种有稳定的月度季节性？ |

### 9.2 数据流

```
DuckDB (bars_history.duckdb, bars_day 表)
    │
    ├──→ adf_mean_reversion.py
    │       └─ ADF 检验 + Hurst 指数 + 半衰期
    │       └─ 输出: CSV + 终端报告
    │
    ├──→ cointegration_pairs.py
    │       └─ 产业链分组 + Engle-Granger 协整检验
    │       └─ 输出: 可交易配对列表 + CSV
    │
    └──→ seasonality_analyzer.py
            └─ 月度收益率 t 检验
            └─ 输出: 季节性交易建议 + CSV
```

### 9.3 实战发现 (2026-07-07 运行)

**30 个连续合约期货的 ADF 检验结果：**

- 7/30 通过 ADF 平稳性检验 (p < 0.10)
- **0/30 的 Hurst 指数 < 0.5** — 全部趋势主导
- 半衰期极短 (1-26 天)，提示 ADF 捕捉的可能是噪声

**结论：期货价格本身不适合均值回归策略。** 验证了 Chan 的核心提醒——均值回归应该用在价差、基差、跨期价差上，而非裸价格。

---

## 10. 与现有引擎的集成

### 10.1 集成点

```
TradingEngine.RunAsync()
    │
    ├─ 预热阶段 (IsWarmup = true)
    │    └─ foreach bar: factor.Update(price)  // 建立因子基线
    │
    ├─ 交易阶段 (IsWarmup = false)
    │    ├─ OnBar(bar):
    │    │    foreach factor: factor.Update(bar.Close)
    │    │    composite = composite.Evaluate()
    │    │    positionRatio = composite.ToPositionRatio()
    │    │    lots = kelly.CalculateLots(equity, price, mult, margin, kellyResult)
    │    │    if risk.CanTrade(): ctx.MarketBuy(inst, lots)
    │    │
    │    └─ OnOrderEvent:
    │         risk.RecordTrade(pnl, equity)  // 更新风险状态
    │
    └─ 日终
         risk.CloseDay(startEquity, endEquity)  // 日终风险追踪
```

### 10.2 与 IStrategy 接口的关系

因子框架不替代 `IStrategy`——它替代的是策略内部的 "指标→信号" 逻辑。

```csharp
// 旧架构 (SmaMacdStrategy)
public void OnBar(Bar bar) {
    sma.Update(price);
    if (price > sma233) ctx.MarketBuy(...);  // bool 信号
}

// 新架构 (FactorStrategy)
public void OnBar(Bar bar) {
    foreach (var f in _factors) f.Update(price);
    var result = _composite.Evaluate();
    var ratio = result.ToPositionRatio(0.20);
    if (Math.Abs(ratio) > 0.05) {
        int lots = _kelly.CalculateLots(...);
        if (_risk.CanTrade()) ctx.MarketBuy(...);
    }
}
```

### 10.3 渐进迁移

```
Phase 2b (当前):
  ✅ 因子框架独立可用
  ✅ 分析工具可以后处理回测结果
  ⏳ 将 WalkForwardValidator 接入 BacktestCommand（批量自动化）
  ⏳ 写 FactorStrategy 示例策略

Phase 3:
  ⏳ 策略用 FactorComposite 替代硬编码指标
  ⏳ IC 分析接入回测报告管线
  ⏳ 参数网格搜索 + StabilityAnalyzer 联动
```

---

## 11. 文件索引

### C# 代码 (`src/TradingStudio.Core/`)

| 文件 | 路径 | 行数 | 职责 |
|------|------|:----:|------|
| `IFactor.cs` | `Factors/` | ~80 | 因子接口 + FactorValue + 枚举 |
| `FactorBase.cs` | `Factors/` | ~160 | 基类：滚动统计、标准化、信号映射 |
| `TrendFactors.cs` | `Factors/` | ~180 | 4 个因子：动量/SMA偏离/波动率/量比 |
| `MomentumFactor.cs` | `Factors/` | ~200 | 独立动量因子 + 横截面排名器 |
| `FactorComposite.cs` | `Factors/` | ~220 | 多因子合成（4 种模式） |
| `FactorEvaluator.cs` | `Analysis/` | ~300 | IC 分析 + 分位数收益 + Spearman |
| `WalkForwardValidator.cs` | `Analysis/` | ~200 | 步进验证窗口 + 稳定性比率 |
| `ParameterStabilityAnalyzer.cs` | `Analysis/` | ~250 | 单/多参数稳定性 + 高原检测 |
| `StrategyScorer.cs` | `Analysis/` | ~200 | 11 维度加权评分 (0-100) |
| `KellyPositionSizer.cs` | `Risk/` | ~150 | 凯利/分数凯利 + 手数计算 |
| `DailyRiskTracker.cs` | `Risk/` | ~200 | 四层风险断路器 + 日终记录 |

### Python 脚本 (`scripts/factor_research/`)

| 文件 | 职责 |
|------|------|
| `adf_mean_reversion.py` | ADF + Hurst + 半衰期三连检验 |
| `cointegration_pairs.py` | 产业链内配对 + Engle-Granger 协整 |
| `seasonality_analyzer.py` | 月度季节性 t 检验 |

### 学习路线文档 (`docs/learning/`)

| 文件 | 职责 |
|------|------|
| `factor-learning-roadmap.md` | 三周学习计划（三本书） |
| `quant-trading-fast-track.md` | 3 天快速通关（Chan 中文版） |

### 记忆文档 (`.claude/projects/.../memory/`)

| 文件 | 职责 |
|------|------|
| `factor-learning-initiative.md` | 因子学习计划持久化记录 |

---

## 变更记录

| 日期 | 变更 |
|------|------|
| 2026-07-07 | 初始版本：11 个 C# 文件 + 3 个 Python 脚本 + 2 个学习文档 |
