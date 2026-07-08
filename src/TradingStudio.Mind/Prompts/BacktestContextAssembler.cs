using System.Text;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Strategy;
using TradingStudio.Engine.Statistics;

namespace TradingStudio.Mind.Prompts;

/// <summary>
/// 回测上下文组装器 — 将 PerformanceReport + StrategyConfig 压缩为 LLM 友好的文本。
///
/// 压缩策略（绝不把原始 Bar 塞入上下文窗口）：
///   - 策略配置: 完整送入（< 2KB）
///   - 绩效指标: Markdown 表格
///   - 权益曲线: 月度桶化 + 关键回撤期定位
///   - 交易记录: Top 5 盈利 + Bottom 5 亏损 + 月度统计
/// </summary>
public class BacktestContextAssembler : IContextAssembler<(PerformanceReport Report, StrategyConfig Config)>
{
    private const string SystemPromptTemplate =
        """
        你是一位经验丰富的量化交易策略分析师。你的任务是基于回测数据，
        找出策略在收益、风险、交易效率、成本敏感性四个维度的优势和缺陷，
        并提出具体可操作的优化建议。

        分析框架：
        1. **收益维度** — CAGR 是否达标？收益是否稳定？月度收益分布是否均匀？
        2. **风险维度** — 最大回撤幅度是否可控？回撤持续期多长？回撤期集中在什么时段？
        3. **交易效率** — 胜率 × 盈亏比是否构成正期望值？平均盈亏金额是否合理？
        4. **成本敏感性** — 手续费和滑点占总收益比例是否合理？降低交易频率能否改善？

        回复要求：
        - 用中文回复
        - 开头用【一句话综合评价】
        - 然后分 "## 优势"、"## 缺陷"、"## 建议" 三个小节
        - 每一条建议必须具体、可操作（不只是"优化参数"，而是"考虑添加 X 来减少 Y"）
        - 最后给出风险评估（低/中/高）和理由
        """;

    private const string QuestionTemplate =
        """
        ## 分析任务

        请从以下四个维度分析上述策略的回测结果：

        1. **收益特征** — CAGR {cagr} 是否达到合格线？收益是否稳健（Sortino {sortino} 反映的下行风险如何）？
        2. **风险特征** — 最大回撤 {maxdd} 是否在可接受范围？回撤修复需要多长时间？
        3. **交易效率** — 胜率 {winrate} 结合盈亏比 {plr}，是否有正期望值？平均盈亏是否对称？
        4. **成本敏感性** — 手续费 {fees} + 滑点 {slippage} 占总利润 {netprofit} 的比例是否合理？
        5. **改进方向** — 针对发现的缺陷，提出 3-5 条具体的、可直接实施的优化方案。

        请用结构化格式回复，每部分用小标题分隔。最后给出**风险等级**（低/中/高）及简要理由。
        """;

    public ContextAssemblyResult Assemble(
        (PerformanceReport Report, StrategyConfig Config) input,
        ContextBudget budget)
    {
        var (report, config) = input;

        var systemPrompt = SystemPromptTemplate;

        var sb = new StringBuilder();

        // 1. 策略概况
        AppendStrategyOverview(sb, report, config);

        // 2. 绩效指标表
        AppendMetricsTable(sb, report);

        // 3. 权益曲线摘要
        AppendEquitySummary(sb, report);

        // 4. 典型交易
        AppendTradeSamples(sb, report);

        // 5. 月度收益分布
        AppendMonthlyBreakdown(sb, report);

        var question = QuestionTemplate
            .Replace("{cagr}", $"{report.CompoundingAnnualReturn:P2}")
            .Replace("{sortino}", $"{report.SortinoRatio:F2}")
            .Replace("{maxdd}", $"{report.MaxDrawdown:P2}")
            .Replace("{winrate}", $"{report.WinRate:P1}")
            .Replace("{plr}", $"{report.ProfitLossRatio:F2}")
            .Replace("{fees}", $"{report.TotalFees:C}")
            .Replace("{slippage}", $"{report.TotalSlippage:C}")
            .Replace("{netprofit}", $"{report.TotalNetProfit:C}");

        var estimatedTokens = EstimateTokens(systemPrompt + sb.ToString() + question);

        return new ContextAssemblyResult
        {
            SystemPrompt = systemPrompt,
            DataContext = sb.ToString(),
            UserQuestion = question,
            EstimatedTokens = estimatedTokens
        };
    }

    // ═══════════════════════════════════════════
    // Section builders
    // ═══════════════════════════════════════════

    private static void AppendStrategyOverview(
        StringBuilder sb, PerformanceReport r, StrategyConfig c)
    {
        var (startDate, endDate) = r.EquityCurve.Count > 0
            ? (r.EquityCurve[0].Time.ToString("yyyy-MM-dd"),
               r.EquityCurve[^1].Time.ToString("yyyy-MM-dd"))
            : ("N/A", "N/A");

        sb.AppendLine("## 策略概况");
        sb.AppendLine();
        sb.AppendLine($"- **策略ID**: {r.StrategyId}");
        sb.AppendLine($"- **策略类型**: {c.StrategyType}");
        sb.AppendLine($"- **交易品种**: {string.Join(", ", c.Instruments)}");
        sb.AppendLine($"- **回测区间**: {startDate} ~ {endDate}");
        sb.AppendLine($"- **初始资金**: {r.StartingCapital:N2}");
        sb.AppendLine($"- **Bar 周期**: {c.BarPeriodMinutes}min");

        // 策略参数
        if (c.Parameters.Count > 0)
        {
            sb.AppendLine("- **策略参数**:");
            foreach (var kvp in c.Parameters)
            {
                sb.AppendLine($"  - {kvp.Key}: {kvp.Value}");
            }
        }

        sb.AppendLine();
    }

    private static void AppendMetricsTable(StringBuilder sb, PerformanceReport r)
    {
        sb.AppendLine("## 绩效指标");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 数值 |");
        sb.AppendLine("|------|------|");
        sb.AppendLine($"| CAGR (年化复合收益率) | {r.CompoundingAnnualReturn:P2} |");
        sb.AppendLine($"| 总净利润 | {r.TotalNetProfit:C} |");
        sb.AppendLine($"| 最终权益 | {r.FinalEquity:C} |");
        sb.AppendLine($"| 最大回撤 | {r.MaxDrawdown:P2} |");
        sb.AppendLine($"| Sharpe 比率 | {r.SharpeRatio:F2} |");
        sb.AppendLine($"| Sortino 比率 | {r.SortinoRatio:F2} |");
        sb.AppendLine($"| 总交易次数 | {r.TotalTrades} |");
        sb.AppendLine($"| 胜率 | {r.WinRate:P1} |");
        sb.AppendLine($"| 平均盈利 | {r.AverageWin:C} |");
        sb.AppendLine($"| 平均亏损 | {r.AverageLoss:C} |");
        sb.AppendLine($"| 盈亏比 (Profit/Loss) | {r.ProfitLossRatio:F2} |");
        sb.AppendLine($"| 总手续费 | {r.TotalFees:C} |");
        sb.AppendLine($"| 总滑点成本 | {r.TotalSlippage:C} |");
        sb.AppendLine($"| 总订单数 | {r.TotalOrders} |");
        sb.AppendLine();
    }

    private static void AppendEquitySummary(StringBuilder sb, PerformanceReport r)
    {
        if (r.EquityCurve.Count < 2) return;

        sb.AppendLine("## 权益曲线摘要");
        sb.AppendLine();

        // 起点 → 终点
        var first = r.EquityCurve[0];
        var last = r.EquityCurve[^1];
        sb.AppendLine($"- 起始权益 ({first.Time:yyyy-MM-dd}): {first.Equity:C}");
        sb.AppendLine($"- 最终权益 ({last.Time:yyyy-MM-dd}): {last.Equity:C}");
        sb.AppendLine($"- 数据点: {r.EquityCurve.Count:N0}");

        // 最大回撤期定位
        var maxDrawdown = r.MaxDrawdown;
        if (maxDrawdown > 0)
        {
            // 找最大回撤的起止点：从峰值到谷底
            // 跳过开头可能为 0 的权益点
            var peak = r.EquityCurve.FirstOrDefault(p => p.Equity > 0);
            if (peak.Equity <= 0) { sb.AppendLine("(权益数据为空)"); sb.AppendLine(); return; }

            var trough = peak;
            decimal peakEquity = peak.Equity;
            decimal maxDD = 0;
            DateTimeOffset ddStart = peak.Time, ddEnd = trough.Time;

            foreach (var point in r.EquityCurve)
            {
                if (point.Equity <= 0) continue;  // 跳过无效点
                if (point.Equity > peakEquity)
                {
                    peakEquity = point.Equity;
                    peak = point;
                }
                var dd = (peakEquity - point.Equity) / peakEquity;
                if ((decimal)dd > maxDD)
                {
                    maxDD = (decimal)dd;
                    ddStart = peak.Time;
                    ddEnd = point.Time;
                    trough = point;
                }
            }

            var ddDays = (ddEnd - ddStart).TotalDays;
            sb.AppendLine($"- 最大回撤期: {ddStart:yyyy-MM-dd} → {ddEnd:yyyy-MM-dd} ({ddDays:F0} 天)");
            sb.AppendLine($"- 回撤期间: 权益从 {peakEquity:C} 降至 {trough.Equity:C}");
        }

        sb.AppendLine();
    }

    private static void AppendTradeSamples(StringBuilder sb, PerformanceReport r)
    {
        if (r.Trades.Count == 0) return;

        var sortedByPnl = r.Trades.OrderByDescending(t => t.PnL).ToList();
        var topWins = sortedByPnl.Where(t => t.PnL > 0).Take(5).ToList();
        var topLosses = sortedByPnl.Where(t => t.PnL <= 0).TakeLast(5).Reverse().ToList();

        sb.AppendLine("## 典型交易");

        if (topWins.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### 最佳 5 笔盈利交易");
            sb.AppendLine();
            sb.AppendLine("| 品种 | 入场日 | 出场日 | 方向 | 入场价 | 出场价 | 盈亏 | 持仓天数 |");
            sb.AppendLine("|------|--------|--------|------|--------|--------|------|----------|");
            foreach (var t in topWins)
            {
                var dir = t.PnL > 0 ? "多" : "空";
                var days = (t.ExitTime - t.EntryTime).TotalDays;
                sb.AppendLine($"| {t.InstrumentId} | {t.EntryTime:yyyy-MM-dd} | {t.ExitTime:yyyy-MM-dd} | {dir} | {t.EntryPrice:F2} | {t.ExitPrice:F2} | {t.PnL:C} | {days:F0} |");
            }
        }

        if (topLosses.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### 最差 5 笔亏损交易");
            sb.AppendLine();
            sb.AppendLine("| 品种 | 入场日 | 出场日 | 方向 | 入场价 | 出场价 | 亏损 | 持仓天数 |");
            sb.AppendLine("|------|--------|--------|------|--------|--------|------|----------|");
            foreach (var t in topLosses)
            {
                var dir = t.PnL > 0 ? "多" : "空";
                var days = (t.ExitTime - t.EntryTime).TotalDays;
                sb.AppendLine($"| {t.InstrumentId} | {t.EntryTime:yyyy-MM-dd} | {t.ExitTime:yyyy-MM-dd} | {dir} | {t.EntryPrice:F2} | {t.ExitPrice:F2} | {t.PnL:C} | {days:F0} |");
            }
        }

        sb.AppendLine();
    }

    private static void AppendMonthlyBreakdown(StringBuilder sb, PerformanceReport r)
    {
        if (r.EquityCurve.Count < 2) return;

        // 按日分组（取每日最后一条权益），然后按月聚合
        var daily = r.EquityCurve
            .GroupBy(p => p.Time.Date)
            .Select(g => g.Last())
            .OrderBy(p => p.Time)
            .ToList();

        if (daily.Count < 2)
        {
            // 单日数据 → 不展示月度分布
            return;
        }

        var monthly = new List<(string Month, decimal StartEquity, decimal EndEquity, decimal PnL, decimal Return, int Trades)>();

        // 按月分组
        var monthlyGroups = daily.GroupBy(p => new { p.Time.Year, p.Time.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month);

        // 统计每月交易数
        var tradesByMonth = r.Trades
            .GroupBy(t => new { t.ExitTime.Year, t.ExitTime.Month })
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var group in monthlyGroups)
        {
            var first = group.First();
            var last = group.Last();
            var pnl = last.Equity - first.Equity;
            var ret = first.Equity > 0 ? (last.Equity - first.Equity) / first.Equity : 0;
            var key = new { first.Time.Year, first.Time.Month };
            var tradeCount = tradesByMonth.TryGetValue(key, out var cnt) ? cnt : 0;

            monthly.Add((
                $"{first.Time.Year}-{first.Time.Month:D2}",
                first.Equity,
                last.Equity,
                pnl,
                ret,
                tradeCount
            ));
        }

        // 只展示最近 36 个月（如果超过的话）
        if (monthly.Count > 36)
            monthly = monthly.Skip(monthly.Count - 36).ToList();

        sb.AppendLine("## 月度收益分布（最近 36 个月）");
        sb.AppendLine();
        sb.AppendLine("| 月份 | 起始权益 | 月收益 | 月收益率 | 交易次数 |");
        sb.AppendLine("|------|----------|--------|----------|----------|");
        foreach (var m in monthly)
        {
            sb.AppendLine($"| {m.Month} | {m.StartEquity:C} | {m.PnL:C} | {m.Return:P2} | {m.Trades} |");
        }

        // 统计
        var winMonths = monthly.Count(m => m.PnL > 0);
        var totalMonths = monthly.Count;
        sb.AppendLine();
        sb.AppendLine($"- 月度胜率: {winMonths}/{totalMonths} ({(double)winMonths / totalMonths:P1})");
        sb.AppendLine($"- 最佳月: {monthly.MaxBy(m => m.PnL).Month} ({monthly.Max(m => m.PnL):C})");
        sb.AppendLine($"- 最差月: {monthly.MinBy(m => m.PnL).Month} ({monthly.Min(m => m.PnL):C})");
        sb.AppendLine();
    }

    // ═══════════════════════════════════════════
    // Token estimation
    // ═══════════════════════════════════════════

    /// <summary>简单 token 估算 — 中文 ~1.5 char/token, 英文 ~4 char/token，取保守值 chars/2</summary>
    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.Length / 2;
    }
}
