# 13 — UI 技术选型：WPF 客户端与 K 线图表方案

> 2026-06-13 | v2.0 全面修订
>
> v1.0 遗漏了 **MVVM 原生支持** 这一关键维度，导致 ScottPlot 5 被错误推荐。
> v2.0 将 MVVM 作为首要评估维度，并补全了 FancyCandles、Syncfusion、ProEssentials、LightningChart 四个候选方案。

---

## 目录

1. [总体方案：WPF 确认](#1-总体方案wpf-确认)
2. [候选方案全景](#2-候选方案全景)
3. [MVVM：关键分水岭](#3-mvvm关键分水岭)
4. [逐方案评估](#4-逐方案评估)
5. [决策矩阵](#5-决策矩阵)
6. [推荐方案](#6-推荐方案)
7. [UI 组件库](#7-ui-组件库)
8. [架构集成](#8-架构集成)
9. [结论](#9-结论)

---

## 1. 总体方案：WPF 确认

### 1.1 两个选项

| 维度 | WPF | Blazor Server |
|------|-----|---------------|
| 渲染模型 | 客户端 GPU 渲染 | 服务端渲染 → SignalR 推 DOM 差分 |
| 延迟 | 亚毫秒（本地） | 10-50ms（网络往返 + DOM 更新） |
| 图表交互 | 原生 Win32 输入，丝滑缩放 | 需 JS 互操作，跨层开销 |
| 实时数据推送 | 内存内事件，零拷贝 | SignalR 序列化 + 反序列化 |
| 开发体验 | XAML + C#，20 年舒适区 | Razor + C#，需学 Web 前端 |
| 跨设备 | 仅 Windows | 任何浏览器 |
| 部署 | 客户端安装 | 浏览器访问，零安装 |

### 1.2 决策

**选择 WPF。**

理由：

1. **实时行情对延迟敏感** — Phase 4 实盘时，CTP Tick 以 500ms 间隔到达，K 线图需要跟着 Tick 实时更新。Blazor Server 的 SignalR 链路（引擎 → Hub → WebSocket → JS → DOM）在 Tick 级高频场景下累积延迟不可忽略
2. **20 年 C# 舒适区** — XAML 数据绑定、Command 模式、依赖属性是你熟悉的范式，学习成本为零
3. **图表库生态** — WPF 有 8 套方案可选（4 开源 + 4 商业）；Blazor 侧只有 Web 图表库（TradingView Lightweight Charts / ECharts），走 JS 互操作有跨语言开销
4. **离线能力** — 回测可视化不需要网络，WPF 本地运行即可
5. **如果以后需要手机看盘** — 可以加一个 Blazor Server 的轻量 Dashboard（只读监控），主力分析界面保留 WPF

> 参考：[Phase 2 部署架构 §15](phase2-backtest-design-v2.md#15-部署架构引擎与-ui-分离) — `TradingStudio.UI.exe` 通过 SignalR 连接引擎进程，获取实时数据

---

## 2. 候选方案全景

### 2.1 开源 / 免费

| 方案 | 授权 | MVVM | K线 | 亮点 | 风险 |
|------|------|------|-----|------|------|
| **OxyPlot** | MIT | ⭐⭐⭐⭐ 原生 PlotModel 绑定 | ⭐⭐⭐ CandleStickSeries | 老牌稳定，NuGet 千万下载 | 维护缓慢 |
| **FancyCandles** | GPL-3.0 | ⭐⭐⭐⭐ 原生 MVVM 设计 | ⭐⭐⭐⭐⭐ 专用 K 线控件 | K 线全部功能开箱即用 | 1 人项目，126 star |
| ScottPlot 5 | MIT | ⭐ 官方不支持 MVVM | ⭐⭐⭐⭐ Candlestick + 非线性时间轴 | API 简洁，开发活跃 | 需自定义 Behavior 桥接 |
| LiveCharts2 | MIT | ⭐⭐⭐⭐ ObservableCollection | ⭐⭐ 基础 K 线 | 动画漂亮，现代化 | 金融专用功能弱 |

### 2.2 商业

| 方案 | 授权 | MVVM | K线 | 价格 | 亮点 |
|------|------|------|-----|------|------|
| **Syncfusion 社区版** | 社区免费 | ⭐⭐⭐⭐ 完整绑定 | ⭐⭐⭐ CandleSeries | **$0**（个人 <$1M 收入） | 全套 800+ 控件 |
| **ProEssentials v10** | **买断** | ⭐⭐⭐ | ⭐⭐⭐⭐ K线 + 布林带 + RSI | 一次性 ~$500-800 | 零拷贝绑定，永不续费 |
| SciChart WPF v8.7 | 订阅 | ⭐⭐⭐⭐ IRenderableSeriesVM | ⭐⭐⭐⭐⭐ 金融图表最强 | ~$1,095/年 | GPU 加速，IndexDateTimeAxis |
| LightningChart | 订阅/买断 | ⭐⭐⭐⭐⭐ 完全可绑定 | ⭐⭐⭐⭐⭐ 全类型 | ~$800+ | 实时性能最强，MVVM 版本 |
| ComponentOne | 订阅 | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ 9 种 K 线变体 | ~$1,000 | 内置技术指标最全 |
| Nevron | 订阅 | ⭐⭐⭐ | ⭐⭐⭐ 股票图表 | ~$900 | 170+ 图表类型 |

> **Syncfusion 社区版条件：** 个人开发者，年收入 < $100 万，团队 ≤ 5 人，员工 ≤ 10 人。你完全符合。无功能限制无水印，每三年续期一次。

---

## 3. MVVM：关键分水岭

K 线图是 `TradingStudio.UI` 的核心 View，不是边角 Dashboard。**MVVM 原生支持与否直接影响架构一致性。**

### 3.1 原生 MVVM 的库（ViewModel 不持有 UI 引用）

```
OxyPlot:
  ViewModel → PlotModel (纯数据对象) → PlotView.Model 绑定
  干净，ViewModel 可单元测试

FancyCandles:
  ViewModel → CandlesSource + VisibleCandlesRange (数据属性)
  干净，专为 WPF MVVM 设计

Syncfusion / SciChart / LightningChart:
  ViewModel → Series ViewModel → Chart.ItemsSource 绑定
  企业级 MVVM，但配置项多
```

### 3.2 不支持 MVVM 的库（需要妥协）

```
ScottPlot 5:
  妥协A: ViewModel 持有 WpfPlot 实例 → 打破 MVVM，VM 引用 UI 控件
  妥协B: 自定义 Behavior<WpfPlot> 桥接  → ~200 行胶水代码，维护负担

官方 FAQ 原话: "ScottPlot does not [support MVVM and data binding]."
推荐方案: "Write your own user control."
警告: "easy to implement, also easy to implement with poor performance."
```

### 3.3 对 TradingStudio 的影响

1. **Phase 2 设计文档强调分层隔离** — 引擎/UI 分离、ViewModel 不碰引擎进程。如果到了图表层反而 ViewModel 要持有 `WpfPlot` 引用，逻辑不自洽
2. **你 20 年 C# WPF** — MVVM 是肌肉记忆，每次 `Plot.Refresh()` 都要意识到自己在打破模式，长期别扭
3. **K 线图会越来越复杂** — 后续需要叠加技术指标、显示买卖点标记、画趋势线、区间统计。MVVM 不干净意味着每加一个交互功能都会放大架构债

**结论：MVVM 原生支持是硬约束，ScottPlot 5 出局。**

---

## 4. 逐方案评估

### 4.1 开源方案

#### OxyPlot（✅ 推荐）

**定位：** 老牌 .NET 图表库，学术/工程领域事实标准。

**MVVM 机制：**

```csharp
// ViewModel — 纯数据，无 UI 引用
public class ChartViewModel : INotifyPropertyChanged
{
    public PlotModel KLineModel { get; set; } = new PlotModel { Title = "K线" };

    public void LoadBars(IReadOnlyList<Bar> bars)
    {
        var series = new CandleStickSeries
        {
            ItemsSource = bars.Select(b => new HighLowItem
            {
                X = b.Time.Ticks,
                High = b.High / 10_000_000.0,
                Low = b.Low / 10_000_000.0,
                Open = b.Open / 10_000_000.0,
                Close = b.Close / 10_000_000.0,
            }).ToList(),
            IncreasingColor = OxyColors.Red,
            DecreasingColor = OxyColors.Green,
        };
        KLineModel.Series.Clear();
        KLineModel.Series.Add(series);
        KLineModel.InvalidatePlot(true);
    }
}
```

```xml
<!-- View — 标准 MVVM 绑定 -->
<oxy:PlotView Model="{Binding KLineModel}" />
```

**优势：**
- **MIT 协议**，完全免费
- **PlotModel 是纯数据对象** — ViewModel 可单元测试，不依赖 UI 线程
- 原生 `CandleStickSeries` + `VolumeSeries` + 技术指标叠加
- 缩放、平移、Tracker（十字光标）内置
- 多轴、多窗格支持
- 导出 PNG/SVG/PDF
- NuGet 下载千万级，社区成熟，Stack Overflow 问题有答案

**劣势：**
- **更新缓慢** — 最近两年主要处于维护模式，重大新功能不太可能出现
- **性能一般** — CPU 渲染，>10 万点交互会有可感知卡顿
- **深色主题需手动配** — 没有内置主题切换，需要配置所有颜色
- **实时更新需自行裁剪窗口** — 没有内置滚动窗口，在 ViewModel 里管理可见数据范围

**Phase 2 适用性：** ⭐⭐⭐⭐ 满足回测可视化所有需求。1 分钟 Bar 一年约 10 万根，OxyPlot 在这个量级表现正常。技术指标只需叠加线图，OxyPlot 完全胜任。

---

#### FancyCandles（✅ 备选推荐）

**定位：** 一个法国开发者专为 WPF 写的 K 线控件。不是通用图表库，就是 K 线图。

> GitHub: [gellerda/FancyCandles](https://github.com/gellerda/FancyCandles) — 126 ⭐, 87 commits, C# 58.4%
> NuGet: `FancyCandles` 2.5.0
> Demo: 微软商店可下载

**内置功能（开箱即用，无需编码）：**
- K 线主图（涨跌颜色、蜡烛宽度、间隙可配）
- 成交量副图（自动跟随涨跌颜色）
- 时间轴 + 价格轴（日线以上只显示日期）
- 滚动条 + 鼠标滚轮缩放（Ctrl+滚轮平移，滚轮缩放）
- 网格线（水平/垂直独立开关）
- 十字光标（横线 + 竖线 + 价格标签）
- 图例（位置/字体/对齐可配）
- 技术指标叠加（可自定义 Add-in）
- Disabled 模式（加载/异步状态时的遮罩）
- 右键属性面板（设置可保存/加载）

**MVVM 机制：**

```csharp
// ViewModel
public class ChartViewModel : INotifyPropertyChanged
{
    public ICandlesSource Candles { get; set; }       // 数据源绑定
    public IntRange VisibleCandlesRange { get; set; } // 可见范围绑定

    public void LoadBars(List<OHLC> bars)
    {
        Candles = new CandlesSource(bars);
        // 控件自动刷新
    }
}
```

```xml
<fc:FancyCandlesChart
    CandlesSource="{Binding Candles}"
    VisibleCandlesRange="{Binding VisibleCandlesRange}" />
```

**优势：**
- **就是为 K 线而生** — 所有 K 线需求都是内置功能，不是你手工攒的
- **MVVM 原生设计** — `CandlesSource` 绑定，`VisibleCandlesRange` 绑定
- **技术指标可扩展** — 内置 + 自定义 Add-in 叠加
- **零开发成本** — Demo 可以直接跑起来看效果

**劣势/风险：**
- **GPL-3.0 授权** — 内部使用免费，如需分发闭源需找作者买商业授权
- **1 人维护** — 126 star，87 提交。如果作者弃坑，你接手代码（C# 58%，代码量不大）
- **社区极小** — Stack Overflow 基本没答案，出了问题只能自己查源码
- **.NET 10 兼容性未知** — 最新 NuGet 2.5.0，需验证是否支持 .NET 10

**Phase 2 适用性：** ⭐⭐⭐⭐ 如果功能恰好满足需求，开发成本最低。GPL-3.0 对个人内部使用无影响。

---

### 4.2 商业方案（排序：推荐度从高到低）

#### Syncfusion 社区版（✅ 可立即使用）

**关键卖点：商业品质 + 个人免费。**

Syncfusion 的 WPF Chart（`SfChart`）有 `CandleSeries`，功能完整。最重要的是 **Community License**：

> 个人开发者 + 年收入 < $100 万 + 团队 ≤ 5 人 → **完全免费，无功能限制、无水印**

你完全符合条件。而且 Syncfusion 不只是图表，是全套 800+ WPF 控件——DataGrid、Ribbon、DockingManager、PropertyGrid 全部包含，等于连 UI 组件库也解决了。

**MVVM 机制：** 企业级绑定，`ItemsSource` + `Series` ViewModel，和 DevExpress/Telerik 同级。

**劣势**：
- 需要在代码里保留 Syncfusion 版权声明（banner 注释）
- 每三年续期一次社区许可证
- 大而全（800+ 控件），但你的 UI 只是轻量管理面板

**Phase 2 适用性：** ⭐⭐⭐⭐⭐ 如果接受商业库，现在就能用，不花钱。

---

#### ProEssentials v10（✅ Phase 4 首选）

**关键卖点：一次性付费，永久使用，不搞订阅。**

| 维度 | SciChart | ProEssentials |
|------|----------|---------------|
| 付费模式 | **年费** ~$1,095 | **买断** ~$500-800 |
| 10 年总成本 | ~$10,950 | ~$500-800 |
| 数据绑定 | 拷贝 | **零拷贝（直接读 float[]）** |
| 性能 | GPU DirectX | GPU Compute Shader |
| 部署体积 | ~30MB | **5-8MB** |
| 授权激活 | 联网激活 | **无激活服务器，无运行时 key** |

**MVVM 机制：** Windows Forms 根底，WPF 通过 WindowsFormsHost 或 NuGet WPF 包。MVVM 支持不如 SciChart/Syncfusion 完善，需要一些胶水代码。

**优势：** 25 年老牌工业库，朴素但强悍。示例包含布林带/RSI/随机指标 + 买卖点标注 + 4 轴同步。

**劣势：** API 风格偏 Win32，学习曲线不平滑。WPF 原生感不强。

**Phase 4 适用性：** ⭐⭐⭐⭐ 对"讨厌被订阅绑架"的个人项目，买断制是理想选择。

---

#### SciChart WPF v8.7（⏸️ Phase 4 备选）

金融图表领域的黄金标准。v8.7 新增 `IndexDateTimeAxis` 解决了金融图表交易日历&非交易时段缺口问题。

**仍然太贵。** $1,095/年对个人发烧友项目不划算。Phase 4 实盘赚钱后可按需升级。

---

#### LightningChart（⏸️ Phase 4 备选）

实时渲染性能之王（DirectX GPU，十亿级数据点 30+ FPS）。有完整的 MVVM 可绑定版本。买断选项可用。

**和 SciChart 定位重叠。** 如果未来要买商业库，二选一对比即可。

---

### 4.3 不推荐方案

| 方案 | 原因 |
|------|------|
| **ScottPlot 5** | 官方不支持 MVVM，"自己写 UserControl" 是架构债 |
| **LiveCharts2 做主 K 线** | 金融专用功能太弱，只适合绩效面板的净值曲线/回撤图 |
| **自绘 SkiaSharp** | K 线渲染 + 坐标轴 + 交互 + 指标 = 至少 3 个月工作量，重复造轮子 |
| **ComponentOne / Nevron** | 订阅制且价格不低，相比 SciChart/LightningChart 无显著优势 |

---

## 5. 决策矩阵

### 5.1 开源方案

| 维度（权重） | OxyPlot | FancyCandles | ScottPlot 5 | LiveCharts2 |
|-------------|---------|-------------|-------------|-------------|
| **MVVM 原生支持 (25%)** | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐ | ⭐⭐⭐⭐ |
| K 线原生功能 (20%) | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐ |
| 性能 (15%) | ⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐ |
| 成本 (15%) | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| 金融专用功能 (10%) | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐ |
| API 易用性 (10%) | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| 维护活跃度 (5%) | ⭐⭐ | ⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **加权总分** | **3.55** | **4.35** | **3.00** | **3.15** |

> MVVM 是硬约束；K 线功能直接影响开发量。MVVM 权重从 0 → 25%，是 v1.0 到 v2.0 的核心修订。

### 5.2 商业方案

| 维度（权重） | Syncfusion 社区 | ProEssentials | SciChart | LightningChart |
|-------------|----------------|--------------|----------|----------------|
| MVVM 原生支持 (25%) | ⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| K 线原生功能 (20%) | ⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| 成本 (20%) | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐ | ⭐⭐⭐ |
| 性能 (15%) | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ |
| 金融专用功能 (10%) | ⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| 可持续性 (10%) | ⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **加权总分** | **3.75** | **3.80** | **3.70** | **4.15** |

---

## 6. 推荐方案

### 6.1 Phase 2（回测可视化）→ OxyPlot 主力 + FancyCandles 备选

```
首推:  OxyPlot (MIT)  — MVVM 最干净，社区最成熟
备选:  FancyCandles (GPL-3.0) — 开发量最少，但社区风险高

不推荐: ScottPlot 5 — MVVM 不支持是架构债
```

**为什么不是 FancyCandles 第一？**

OxyPlot 虽然功能不如 FancyCandles 开箱即用，但有两个决定性优势：

1. **社区和生态** — NuGet 千万下载，Stack Overflow 上千问答，遇到问题 90% 有现成答案。FancyCandles 只有 1 个人，126 star，遇到问题只能自己翻源码
2. **扩展自由度** — OxyPlot 是通用图表框架，你可以画任何东西。FancyCandles 是专用 K 线控件，功能虽全，但万一哪里不满足（比如叠加自定义绘图），修改空间有限

**技术验证项（Phase 2a 初期完成）：**

| # | 验证项 | OxyPlot 预期 | 不通过时的 fallback |
|---|--------|-------------|-------------------|
| ① | 1 万根 K 线缩放/平移流畅度 | 流畅 | FancyCandles |
| ② | 十字光标 + 价格/时间标签 | Tracker 内置 | 自定义 Tracker |
| ③ | 成交量副图 + 指标副图同步滚动 | 多轴联动 | FancyCandles |
| ④ | 深色主题适配 | 手动配色 1 天 | FancyCandles 内置 |
| ⑤ | OHLC 数据绑定到 PlotModel | PlotModel 纯数据 | — |

### 6.2 绩效面板 → LiveCharts2

策略绩效面板（净值曲线、回撤图、月度收益柱状图）不需要金融专用功能，LiveCharts2 的动画效果和现代化风格更适合。

```
K 线主图:  OxyPlot                        ← ✅ 已实现 (2026-06-14)
绩效面板:  LiveCharts2                      ← ⏸️ Phase 3
数据表格:  HandyControl DataGrid           ← ⏸️ Phase 3
SignalR:   EngineHub Client               ← ⏸️ Phase 3 (NuGet 包已就位)
```
```

### 6.3 Phase 4（实盘监控）升级路径

```
Phase 4 升级条件：策略实盘有正期望 + 对当前图表有明确不满

如果不满的是 MVVM 复杂度:
  → Syncfusion 社区版 ($0)  ← 先试这个

如果不满的是性能:
  → ProEssentials (买断 ~$500-800)  ← 不订阅
  → LightningChart (买断选项)       ← 性能最强

如果不满的是功能（技术指标不够全）:
  → SciChart WPF (~$1,095/年)      ← 金融图表最强

大概率不需要升级:
  OxyPlot 在回测可视化场景下完全够用
  实盘监控也是看 Bar，不是看 Tick，数据量不爆炸
```

### 6.4 切换成本

所有方案都通过 MVVM 隔离了图表库依赖：

```
ViewModel — 纯数据，不引用任何图表库
    ↓ 绑定
View — XAML 中引用具体图表控件

切换 = 改 View 层 XAML + 少量 Adapter 代码
预估工时: 2-3 天
```

---

## 7. UI 组件库

### 7.1 候选

| 方案 | 授权 | 控件数 | 深色主题 | .NET 10 | 备注 |
|------|------|--------|---------|---------|------|
| **HandyControl 3.5** | MIT | 80+ | ✅ 原生 | ✅ | 国内 WPF 首选 |
| **Syncfusion 社区版** | 社区免费 | 800+ | ✅ 原生 | ✅ | 与图表库统一，但大而全 |
| MaterialDesignInXAML | MIT | 60+ | ✅ 原生 | ✅ | Google MD 风格 |

### 7.2 推荐：HandyControl

- 控件最丰富（80+），精准覆盖管理面板需求
- 深色主题一行切换：`ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark`
- 中文社区活跃，文档和示例多
- MIT 协议，零成本
- 轻量（不像 Syncfusion 800+ 控件那样重型）

> 如果用 Syncfusion 社区版做图表，那 UI 组件库也可以统一用 Syncfusion，减少依赖数量。但 HandyControl 更轻量、更接地气。**两个都行，看是否愿意接受 Syncfusion 的庞大依赖。**

---

## 8. 架构集成

### 8.1 MVVM 分层

```
TradingStudio.UI/
├── Views/
│   ├── MainWindow.xaml              ← HandyControl GlowWindow
│   ├── DashboardView.xaml           ← 总览面板
│   ├── ChartView.xaml               ← OxyPlot PlotView (K线主图)
│   ├── PerformanceView.xaml         ← LiveCharts2 (净值曲线/回撤图)
│   ├── StrategyListView.xaml        ← 策略列表
│   ├── OrderListView.xaml           ← 订单监控
│   └── AlertView.xaml               ← 告警面板
├── ViewModels/
│   ├── MainViewModel.cs
│   ├── ChartViewModel.cs            ← PlotModel + CandleStickSeries (纯数据!)
│   ├── PerformanceViewModel.cs
│   ├── StrategyListViewModel.cs
│   └── ...
├── Services/
│   ├── EngineHubClient.cs           ← SignalR 客户端
│   └── ChartDataAdapter.cs          ← Bar[] → ObservableCollection<HighLowItem>
├── Models/
│   └── HighLowItem.cs               ← OxyPlot OHLC 适配
└── Converters/
    └── ...
```

### 8.2 K 线图数据流

```
TradingEngine (引擎进程)
    │
    │  SignalR Hub (localhost:5199)
    │
    ▼
EngineHubClient (UI 进程)
    │  Bar[] → HighLowItem[] 转换
    │
    ▼
ChartViewModel.KLineModel.Series
    │  PlotModel.InvalidatePlot(true)
    │
    ▼
OxyPlot PlotView (View 层，只读)
    ├── 主窗格: CandleStickSeries + LineSeries (MA overlay)
    ├── 副窗格1: VolumeSeries (涨红跌绿)
    └── 副窗格2: LineSeries (MACD / RSI)
```

### 8.3 两种模式下图表的数据来源

| 模式 | 数据来源 | 更新方式 |
|------|---------|---------|
| **回测可视化** | 回测引擎直接输出 `Bar[]`，本地内存 | 一次性加载全部 → 按回放进度滚动窗口 |
| **实盘监控** | SignalR 推送实时 Bar | 增量更新最后一根 Bar 的 Close → 新 Bar 完成时左移窗口 |

---

## 9. 结论

```
┌───────────────────────────────────────────────────────────┐
│  TradingStudio.UI 技术栈 (v2.0)                            │
│                                                            │
│  框架:    WPF (.NET 10, x64)                               │
│  UI 库:   HandyControl 3.5 (MIT)                           │
│  K线图:   OxyPlot (MIT) ← Phase 2 主力                     │
│  备选:    FancyCandles (GPL-3.0) ← OxyPlot 不满足时切换     │
│  绩效图:  LiveCharts2 (MIT) ← 净值曲线/回撤图               │
│  升级:    Syncfusion 社区版 / ProEssentials / SciChart      │
│           ← Phase 4 按需选择                                │
│  架构:    MVVM，PlotModel 纯数据绑定                        │
│           ViewModel 不持有任何图表库 UI 引用                 │
│  通信:    SignalR Client → EngineHub (localhost:5199)       │
└───────────────────────────────────────────────────────────┘
```

**核心原则：**

1. **MVVM 是硬约束** — ScottPlot 5 出局的唯一原因。图表库必须支持 ViewModel 纯数据绑定
2. **不提前为用不上的性能买单** — 回测阶段 OxyPlot 完全够用
3. **备选是保险，不是负担** — FancyCandles 已验证 OK，切换成本 2-3 天
4. **先跑通再优化** — Phase 2 第一优先级是回测引擎正确性，K 线图是辅助调试工具
5. **买断 > 订阅** — 如果将来要付费，优先选 ProEssentials 这种不续费的

---

> **实施状态 (2026-06-14):**
> - ✅ OxyPlot K线 Demo 已完成 (四窗格 + 五指标 + 实时模拟)
> - ⏸️ HandyControl / LiveCharts2 / SignalR Client — 待 Phase 3 实施
> - ⏸️ FancyCandles 备选方案 — 暂未验证，OxyPlot 目前满足需求
>
> **相关文档：**
> - [Phase 2 回测系统设计 v2.1](design/phase2-backtest-design-v2.md) — 引擎/UI 分离架构（§15）
> - [TradingStudio 架构设计精简版](design/TradingStudio架构设计-精简版.md) — 7 项目结构
> - [数据库架构选型](design/10-data-model-reconciled.md)
