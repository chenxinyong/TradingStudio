# 策略因子学习路线图

> 三周精读 + 代码实战，从 "指标→信号" 升级到 "因子→策略组合"
> 启动日期：2026-07-05

---

## 为什么要学因子

当前 TradingStudio 的所有策略都是 "指标→信号" 的直接映射：

```
Bar → SMA(233) → 价格穿越? → 入场/出场  (bool 信号，无强度概念)
```

因子思维把这根链条拆开：

```
Bar → 动量因子(连续值) → 标准化(z-score/百分位) → 因子合成(加权/等权)
    → 波动率因子(连续值) → 标准化              ↗
    → 价值因子(连续值)   → 标准化          → 信号强度(0~1) → 仓位映射 → 入场
```

**升级后你能做的事：**
- 回答 "这个信号有多强？" 而不是 "有没有信号？"
- 仓位 = 信号强度 × 最大仓位，而不是固定手数
- 多个因子共同决策，而不是单个指标孤注一掷
- 用 IC 分析评估因子质量，而不是只能看回测 Sharpe

---

## 三周学习计划

### Week 1: Ernie Chan《Algorithmic Trading》— 回测方法论与数据基础

> 核心命题：回测结果是真实的还是过拟合的？

| 天 | 章节 | 核心概念 | 代码练习 |
|----|------|---------|---------|
| 1 | Ch1-2: Backtesting | 回测偏差类型、样本内外划分、前视偏差 | 写一个 `WalkForwardValidator` 类，自动切分 IS/OOS |
| 2 | Ch3: Strategy Development | 策略开发流程、参数优化陷阱 | 实现 `ParameterStabilityAnalyzer`：参数 ±20% 扰动后 Sharpe 分布 |
| 3 | Ch4: Mean Reversion | 平稳性检验(ADF)、半衰期、协整 | 在 Python 里对 Top30 品种做 ADF 检验，计算半衰期分布 |
| 4 | Ch5: Momentum | 时间序列动量 vs 横截面动量 | 实现 `MomentumFactor`：`return(t, t-N)` 标准化到 z-score |
| 5 | Ch6: Kalman Filter | 动态对冲比率、状态空间模型 | （选做）用 Kalman 滤波器估计配对交易动态比率 |
| 6 | Ch7: Intraday | 日内季节性、成交量加权 | 分析 Top5 品种的日内成交量分布（早盘/午盘/夜盘） |
| 7 | 回顾 + 实战 | 一周代码整合 | 把 WalkForward + ParameterStability 集成到 BacktestCommand |

**Week 1 交付物：**
- `TradingStudio.Core/Factors/MomentumFactor.cs` — 时间序列动量因子
- `TradingStudio.Core/Analysis/WalkForwardValidator.cs` — 步进验证器
- `scripts/factor_research/adf_mean_reversion.py` — ADF 检验脚本
- 知识库笔记：`03-Strategies/因子研究-动量与均值回归基础.md`

---

### Week 2: Wesley Gray《Quantitative Momentum》— 因子构建完整思维过程

> 核心命题：动量因子不是随便算个收益率——从宇宙筛选到因子组合的全链条

| 天 | 章节 | 核心概念 | 代码练习 |
|----|------|---------|---------|
| 8 | Ch1-3: Momentum Premium | 动量溢价的学术证据、行为金融解释 | 在最活跃 30 品种上计算 1M/3M/6M/12M 动量收益分布 |
| 9 | Ch4: Universe | 宇宙筛选（流动性、市值、可交易性） | 实现 `UniverseFilter`：按成交量/持仓量筛选可交易品种 |
| 10 | Ch5: Momentum Measure | 动量度量（总收益、超额收益、风险调整） | 实现 `MomentumFactor` 的 3 种变体：Raw / Residual / Sharpe-adj |
| 11 | Ch6: Factor Combination | 多因子合成（等权/波动率加权/最优） | 实现 `CompositeFactor`：支持等权、波动率倒数加权、IC 加权 |
| 12 | Ch7: Portfolio | 从因子得分到投资组合（分位数/排名加权） | 实现 `FactorPortfolioBuilder`：Top-N 选品种 + 得分加权仓位 |
| 13 | Ch8-9: Implementation | 再平衡频率、交易成本、税收 | 在回测引擎中加入因子再平衡逻辑（日频/周频切换） |
| 14 | 回顾 + 实战 | 一周代码整合 | 端到端：Universe → Factor → Composite → Portfolio 完整链路 |

**Week 2 交付物：**
- `TradingStudio.Core/Factors/CompositeFactor.cs` — 多因子合成框架
- `TradingStudio.Core/Factors/UniverseFilter.cs` — 品种宇宙筛选
- `TradingStudio.Core/Portfolio/FactorPortfolioBuilder.cs` — 因子组合构建器
- 知识库笔记：`03-Strategies/因子研究-动量因子构建全链条.md`

---

### Week 3: Andreas Clenow《Stocks on the Move》— 从因子到策略组合

> 核心命题：单个因子不够，多个策略组合才是正解

| 天 | 章节 | 核心概念 | 代码练习 |
|----|------|---------|---------|
| 15 | Ch1-3: Strategy Design | 趋势跟随因子、波动率调整 | 实现 `TrendFactor`：突破 N 日高/低 + ATR 标准化 |
| 16 | Ch4-5: Risk Management | 波动率目标仓位、相关性矩阵 | 实现 `VolatilityTargetSizer`：仓位 = 目标波动率 / 实现波动率 |
| 17 | Ch6-7: Portfolio | 等风险贡献、最大回撤控制 | 实现 `RiskParityAllocator`：按风险贡献分配资金 |
| 18 | Ch8-9: Execution | 订单管理、滑点模型 | 在 Simulator 中加入 `AlmgrenChrissSlippage` 滑点模型 |
| 19 | Ch10-11: Monitoring | 策略监控、退役标准 | 实现 `StrategyMonitor`：滚动 Sharpe / 滚动 MaxDD 告警 |
| 20 | Ch12: Putting It Together | 完整系统组装 | 写一个 `FactorStrategy`，组合动量+趋势+波动率三因子 |
| 21 | 回顾 + 实战 | 三周代码整合 + 全品种回测 | 端到端批量回测：30 品种 × 3 因子组合，出综合报告 |

**Week 3 交付物：**
- `TradingStudio.Core/Factors/TrendFactor.cs` — 趋势因子
- `TradingStudio.Core/Risk/VolatilityTargetSizer.cs` — 波动率目标仓位
- `TradingStudio.Core/Monitoring/StrategyMonitor.cs` — 策略监控
- `TradingStudio.Strategy/FactorStrategy.cs` — 因子驱动的完整策略
- 知识库笔记：`03-Strategies/因子研究-多策略组合与风险管理.md`

---

## 知识库笔记结构

在 Obsidian `03-Strategies/` 下新建：

```
03-Strategies/
├── 因子研究-动量与均值回归基础.md       # Week 1 产出
├── 因子研究-动量因子构建全链条.md        # Week 2 产出
├── 因子研究-多策略组合与风险管理.md      # Week 3 产出
└── 因子研究-因子库总览.md               # 因子目录索引
```

每篇笔记遵循策略模板（`_模板_策略研究.md`），额外增加：

- **因子定义** — 公式、输入数据、输出范围
- **IC 分析** — Rank IC / Pearson IC，IC 衰减曲线
- **分位数收益** — Q1-Q5 分组收益，多空收益差
- **因子相关性矩阵** — 与其他因子的相关性
- **稳健性** — 参数敏感度，不同品种/时段的表现

---

## 代码架构演进

学习完成后的目标架构：

```
TradingStudio.Core/
├── Factors/                          ← 新增：因子层
│   ├── IFactor.cs                    ← 因子接口
│   ├── FactorBase.cs                 ← 基类（标准化、缺失值处理）
│   ├── MomentumFactor.cs            ← 动量因子
│   ├── TrendFactor.cs               ← 趋势因子
│   ├── VolatilityFactor.cs          ← 波动率因子
│   ├── CompositeFactor.cs           ← 因子合成（等权/IC加权/波动率加权）
│   └── FactorNormalizer.cs          ← 标准化（z-score / percentile / rank）
│
├── Universe/                         ← 新增：品种筛选
│   ├── IUniverseFilter.cs
│   ├── LiquidityFilter.cs           ← 成交量/持仓量筛选
│   └── ContinuityFilter.cs          ← 上市时间/数据连续性筛选
│
├── Analysis/                         ← 新增：分析工具
│   ├── IcAnalyzer.cs                ← IC 分析（Rank IC, IC IR）
│   ├── QuantileAnalyzer.cs          ← 分位数收益分析
│   ├── FactorDecayAnalyzer.cs       ← 因子衰减分析
│   └── WalkForwardValidator.cs      ← 步进验证
│
├── Portfolio/                        ← 新增：组合构建
│   ├── FactorPortfolioBuilder.cs    ← 因子→组合
│   └── RiskParityAllocator.cs       ← 风险平价
│
├── Risk/                             ← 扩展：仓位管理
│   └── VolatilityTargetSizer.cs     ← 波动率目标
│
└── Monitoring/                       ← 新增：策略监控
    └── StrategyMonitor.cs           ← 滚动指标告警

TradingStudio.Strategy/
└── FactorStrategy.cs                 ← 使用因子框架的策略示例
```

---

## 学习节奏建议

```
每天: 1-2h 阅读 + 1-2h 写代码 + 30min 记笔记

Week 1 ──────── Week 2 ──────── Week 3
 ██████████████ ██████████████ ██████████████
 回测基础         因子构建         策略组合
 数据陷阱         因子合成         风险管理
 ADF/协整         Universe         滑点/监控
```

**关键原则：**
1. **先跑通再优化** — 不要追求完美实现，功能跑通就继续
2. **代码服务于理解** — 写代码是为了吃透概念，不是为了交付产品
3. **每周末做回顾** — 把笔记整理进 Obsidian 知识库
4. **不跳过数据验证** — Week 1 的 ADF 检验和半衰期分析是最容易被跳过的，但最关键

---

## 三周后的能力升级

| 维度 | 当前 | 三周后 |
|------|------|--------|
| 信号类型 | bool（有/无） | double（强度 0~1） |
| 决策依据 | 单一指标 | 多因子合成 |
| 仓位管理 | 固定比例 | 信号强度 × 波动率目标 |
| 因子评估 | 看回测 Sharpe | IC 分析 + 分位数收益 + 衰减曲线 |
| 参数选择 | 经验值 | 参数平原扫描 + 稳健性检验 |
| 策略监控 | 无 | 滚动指标 + 退役标准 |
| 品种选择 | 手动指定 | 流动性筛选 + 因子排名 |

---

## 参考资源

- 教材：Ernie Chan《Algorithmic Trading》, Wesley Gray《Quantitative Momentum》, Andreas Clenow《Stocks on the Move》
- Lean 源码：`C:\Works\ClaudeCode\Lean\` — `Indicators/`, `Algorithm/Framework/` (Alpha/Universe/Portfolio 模型)
- 现有代码：`TradingStudio.Strategy/SmaMacdStrategy.cs` — 当前策略模式，改造的出发点
- 知识库：`03-Strategies/_模板_策略研究.md` — 策略文档模板，因子研究笔记沿用此格式
