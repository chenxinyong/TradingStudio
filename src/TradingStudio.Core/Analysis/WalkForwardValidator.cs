namespace TradingStudio.Core.Analysis;

/// <summary>
/// 步进验证器 — Ernie Chan《Algorithmic Trading》Ch1-2 的核心概念。
///
/// 核心问题：你的策略参数在样本外还能赚钱吗？
///
/// 两种模式：
///   Rolling   — 固定窗口滚动（例如 2年训练 → 1年测试 → 滑动1年 → 重复）
///   Anchored  — 固定起点，不断扩大训练窗口（2年→3年→4年…）
///
/// 关键指标：
///   OOS/IS Sharpe 比率 — 接近 1.0 则稳健，&lt;0.5 则过拟合
///   OOS Sharpe 标准差 — 越小说明策略在不同市场环境越稳定
/// </summary>
public class WalkForwardValidator
{
    private readonly WalkForwardConfig _config;

    public WalkForwardValidator(WalkForwardConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// 生成步进窗口列表。每个窗口包含 IS (训练) 和 OOS (测试) 的时间范围。
    /// 策略在 IS 上优化参数，在 OOS 上验证——模拟真实交易中"不知道未来"的约束。
    /// </summary>
    public List<WalkForwardWindow> GenerateWindows(DateTime dataStart, DateTime dataEnd)
    {
        var windows = new List<WalkForwardWindow>();
        var totalDays = (dataEnd - dataStart).Days;
        var windowDays = (int)(_config.IsPeriod.TotalDays + _config.OosPeriod.TotalDays);
        var stepDays = (int)_config.StepSize.TotalDays;

        if (windowDays > totalDays)
            throw new InvalidOperationException(
                $"数据范围 ({totalDays} 天) 小于单个窗口 ({windowDays} 天)。请缩小 IS/OOS 周期或扩大数据范围。");

        var windowStart = dataStart;
        int windowIndex = 0;

        while (windowStart.AddDays(windowDays) <= dataEnd)
        {
            var isStart = windowStart;
            var isEnd = isStart.Add(_config.IsPeriod);
            var oosStart = isEnd;
            var oosEnd = oosStart.Add(_config.OosPeriod);

            // Anchored 模式：IS 窗口不断扩大（起点固定）
            if (_config.Mode == WalkForwardMode.Anchored)
                isStart = dataStart;

            windows.Add(new WalkForwardWindow
            {
                Index = windowIndex,
                IsStart = isStart,
                IsEnd = isEnd,
                OosStart = oosStart,
                OosEnd = oosEnd,
                Label = $"W{windowIndex}: IS=[{isStart:yyyy-MM-dd}, {isEnd:yyyy-MM-dd}] OOS=[{oosStart:yyyy-MM-dd}, {oosEnd:yyyy-MM-dd}]"
            });

            windowStart = windowStart.Add(_config.StepSize);
            windowIndex++;
        }

        return windows;
    }

    /// <summary>
    /// 汇总所有窗口的结果，计算步进验证核心指标。
    /// </summary>
    public WalkForwardSummary Summarize(
        IReadOnlyList<WalkForwardWindow> windows,
        IReadOnlyList<WindowPerformance> performances)
    {
        if (windows.Count != performances.Count)
            throw new ArgumentException($"窗口数 ({windows.Count}) 与性能记录数 ({performances.Count}) 不匹配");

        var validWindows = windows.Zip(performances, (w, p) => (Window: w, Perf: p))
            .Where(x => x.Perf.IsValid)
            .ToList();

        if (validWindows.Count == 0)
            return new WalkForwardSummary { IsEmpty = true };

        var oosSharpes = validWindows.Select(x => x.Perf.OosSharpe).ToList();
        var isSharpes = validWindows.Select(x => x.Perf.IsSharpe).ToList();

        var oosMean = oosSharpes.Average();
        var oosStd = Math.Sqrt(oosSharpes.Average(s => Math.Pow(s - oosMean, 2)));

        var isMean = isSharpes.Average();

        // 关键指标：OOS/IS Sharpe 比率
        // Chan 的建议：&gt;0.7 可接受，&gt;0.8 良好，&gt;0.9 优秀
        var stabilityRatio = isMean != 0 ? oosMean / isMean : 0;

        // OOS 正 Sharpe 窗口比例
        var positiveRatio = (double)oosSharpes.Count(s => s > 0) / oosSharpes.Count;

        // OOS 最差表现
        var worstOos = oosSharpes.Min();
        var worstWindow = validWindows[oosSharpes.IndexOf(worstOos)];

        // 一致性评分 (Chan 的 "robustness score"):
        // = StabilityRatio × PositiveRatio × (1 - CV)
        // CV = OOS Sharpe 变异系数 = std/|mean|
        var cv = oosMean != 0 ? oosStd / Math.Abs(oosMean) : double.PositiveInfinity;
        var robustnessScore = stabilityRatio * positiveRatio * Math.Max(0, 1 - cv);

        return new WalkForwardSummary
        {
            TotalWindows = windows.Count,
            ValidWindows = validWindows.Count,
            OosSharpeMean = oosMean,
            OosSharpeStd = oosStd,
            IsSharpeMean = isMean,
            StabilityRatio = stabilityRatio,
            OosPositiveRatio = positiveRatio,
            OosSharpeCV = cv,
            WorstOosSharpe = worstOos,
            WorstWindowLabel = worstWindow.Window.Label,
            RobustnessScore = robustnessScore,
            WindowDetails = validWindows.Select(x => new WindowDetail
            {
                Label = x.Window.Label,
                IsSharpe = x.Perf.IsSharpe,
                OosSharpe = x.Perf.OosSharpe,
                IsWinRate = x.Perf.IsWinRate,
                OosWinRate = x.Perf.OosWinRate,
                IsTrades = x.Perf.IsTradeCount,
                OosTrades = x.Perf.OosTradeCount,
            }).ToList(),

            Verdict = robustnessScore switch
            {
                >= 0.7 => WalkForwardVerdict.Robust,
                >= 0.5 => WalkForwardVerdict.Acceptable,
                >= 0.3 => WalkForwardVerdict.Fragile,
                _ => WalkForwardVerdict.Overfit,
            }
        };
    }
}

/// <summary>步进验证配置</summary>
public class WalkForwardConfig
{
    /// <summary>训练窗口长度（In-Sample，用于参数优化）</summary>
    public TimeSpan IsPeriod { get; init; } = TimeSpan.FromDays(365 * 2); // 默认 2 年

    /// <summary>测试窗口长度（Out-of-Sample，用于验证）</summary>
    public TimeSpan OosPeriod { get; init; } = TimeSpan.FromDays(365); // 默认 1 年

    /// <summary>每次滑动的步长</summary>
    public TimeSpan StepSize { get; init; } = TimeSpan.FromDays(365 / 2); // 默认半年

    /// <summary>窗口模式</summary>
    public WalkForwardMode Mode { get; init; } = WalkForwardMode.Rolling;
}

/// <summary>窗口模式</summary>
public enum WalkForwardMode
{
    /// <summary>固定窗口滚动（Chan 推荐）：每步丢弃最早的 IS 数据</summary>
    Rolling,

    /// <summary>固定起点扩展：IS 窗口不断扩大，适合检验策略是否因市场变化失效</summary>
    Anchored,
}

/// <summary>单个步进窗口</summary>
public class WalkForwardWindow
{
    public int Index { get; init; }
    public DateTime IsStart { get; init; }
    public DateTime IsEnd { get; init; }
    public DateTime OosStart { get; init; }
    public DateTime OosEnd { get; init; }
    public string Label { get; init; } = "";

    public override string ToString() => Label;
}

/// <summary>单个窗口的 IS/OOS 性能数据（由回测引擎填入）</summary>
public class WindowPerformance
{
    // IS (训练集) 表现
    public double IsSharpe { get; init; }
    public double IsWinRate { get; init; }
    public int IsTradeCount { get; init; }

    // OOS (测试集) 表现
    public double OosSharpe { get; init; }
    public double OosWinRate { get; init; }
    public int OosTradeCount { get; init; }

    public bool IsValid => IsTradeCount >= 5 && OosTradeCount >= 5;
}

/// <summary>步进验证汇总报告</summary>
public class WalkForwardSummary
{
    public bool IsEmpty { get; init; }

    public int TotalWindows { get; init; }
    public int ValidWindows { get; init; }

    /// <summary>OOS Sharpe 均值 — 策略在样本外的核心表现</summary>
    public double OosSharpeMean { get; init; }

    /// <summary>OOS Sharpe 标准差 — 越小越稳定</summary>
    public double OosSharpeStd { get; init; }

    /// <summary>IS Sharpe 均值 — 策略在训练集的表现（几乎总是比 OOS 高）</summary>
    public double IsSharpeMean { get; init; }

    /// <summary>
    /// 稳定性比率 = OOS_Sharpe / IS_Sharpe。
    /// Chan 基准：>0.7 可接受，>0.8 良好，>0.9 优秀。
    /// <0.5 强烈提示过拟合。
    /// </summary>
    public double StabilityRatio { get; init; }

    /// <summary>OOS 窗口中 Sharpe 为正的比例</summary>
    public double OosPositiveRatio { get; init; }

    /// <summary>OOS Sharpe 变异系数 (CV = std/|mean|) — 越小越稳定</summary>
    public double OosSharpeCV { get; init; }

    /// <summary>最差窗口的 OOS Sharpe</summary>
    public double WorstOosSharpe { get; init; }

    /// <summary>最差窗口的标签</summary>
    public string WorstWindowLabel { get; init; } = "";

    /// <summary>
    /// 稳健性综合评分 (Chan 方法):
    /// = StabilityRatio × PositiveRatio × (1 - CV)
    /// 范围: [-∞, 1.0]，越高越好
    /// </summary>
    public double RobustnessScore { get; init; }

    public WalkForwardVerdict Verdict { get; init; }
    public List<WindowDetail> WindowDetails { get; init; } = [];
}

/// <summary>单窗口详情</summary>
public class WindowDetail
{
    public string Label { get; init; } = "";
    public double IsSharpe { get; init; }
    public double OosSharpe { get; init; }
    public double IsWinRate { get; init; }
    public double OosWinRate { get; init; }
    public int IsTrades { get; init; }
    public int OosTrades { get; init; }

    /// <summary>Sharpe 衰减 = OOS/IS — 这个窗口的过拟合程度</summary>
    public double SharpeDecay => IsSharpe != 0 ? OosSharpe / IsSharpe : 0;
}

/// <summary>步进验证结论</summary>
public enum WalkForwardVerdict
{
    /// <summary>稳健 — OOS 表现接近 IS，策略在不同时期表现一致</summary>
    Robust,

    /// <summary>可接受 — 有些衰减但整体仍盈利</summary>
    Acceptable,

    /// <summary>脆弱 — OOS 大幅衰减，或者在部分窗口亏损</summary>
    Fragile,

    /// <summary>过拟合 — OOS 几乎不赚钱，参数是曲线拟合的产物</summary>
    Overfit,
}
