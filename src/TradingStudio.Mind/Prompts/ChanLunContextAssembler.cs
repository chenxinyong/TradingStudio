using System.Text;
using TradingStudio.Strategy.ChanLun;

namespace TradingStudio.Mind.Prompts;

/// <summary>
/// 缠论上下文组装器 — 将 ChanLunResult（分型/笔/中枢/趋势）压缩为 LLM 友好文本。
/// 核心思想：不送原始 K 线，而是把缠论结构对象描述为自然语言。
/// </summary>
public class ChanLunContextAssembler : IContextAssembler<(ChanLunResult Result, string InstrumentId, string Timeframe)>
{
    private const string SystemPrompt =
        """
        你是一位精通缠中说禅理论的量化交易分析师。你的任务是基于缠论结构数据
        （分型、笔、中枢、趋势方向），解读当前市场结构，识别关键买卖点，
        并给出具体可操作的交易建议。

        缠论核心概念：
        - **分型 (Fractal)**: 顶分型 = 三根K线中间最高；底分型 = 三根K线中间最低
        - **笔 (Bi)**: 连接相邻异类分型，是趋势的基本单位
        - **中枢 (Zhongshu)**: 连续三笔重叠区域，是震荡密集区 (Zg=上沿, Zd=下沿, Zz=中轨)
        - **背驰**: 趋势力度衰减 → 反转信号。通过比较相邻同向笔的力度判断
        - **买卖点**:
          - 一买/一卖: 趋势背驰后的第一类买卖点（最可靠）
          - 二买/二卖: 回调/反弹不破前低/前高
          - 三买/三卖: 突破中枢后回踩不破中枢上沿/下沿

        回复要求：
        - 用中文回复，简明但专业
        - 先给【当前结构概述】（1-2句）
        - 再分 ## 中枢分析、## 笔分析、## 买卖点判断、## 交易建议
        - 交易建议包含：方向、入场区间、止损位、目标位、仓位建议
        - 如果结构不清晰，诚实说明，不要强行给建议
        """;

    private const string QuestionTemplate =
        """
        ## 分析任务

        请解读上述 {instrument} 的 {timeframe} 缠论结构，回答以下问题：

        1. **当前趋势**：趋势方向是什么？力度如何（比较最近同向笔的 Power 值）？
        2. **中枢状态**：价格相对于中枢的位置（上方/下方/内部）？中枢的支撑/压力作用？
        3. **买卖点**：是否有可识别的缠论买卖点？如果有，是第几类？
        4. **交易建议**：基于上述分析，给出一条具体的交易建议
           （方向、入场区间、止损位、目标位、仓位%）

        请按结构化格式回复。
        """;

    public ContextAssemblyResult Assemble(
        (ChanLunResult Result, string InstrumentId, string Timeframe) input,
        ContextBudget budget)
    {
        var (result, instrumentId, timeframe) = input;
        var sb = new StringBuilder();

        // 1. 结构概述
        sb.AppendLine($"## {instrumentId} {timeframe} 缠论结构");
        sb.AppendLine();
        sb.AppendLine($"- 趋势方向: {TranslateTrend(result.Trend)}");
        sb.AppendLine($"- 标准K线: {result.StdCount} 根");
        sb.AppendLine($"- 分型: {result.FractalCount} 个 (顶{result.Fractals.Count(f => f.Type == FractalType.Top)} 底{result.Fractals.Count(f => f.Type == FractalType.Bottom)})");
        sb.AppendLine($"- 笔: {result.BiCount} 笔");
        sb.AppendLine($"- 中枢: {result.ZhongshuCount} 个");
        sb.AppendLine();

        // 2. 最新笔列表（最近 10 笔）
        AppendBiSummary(sb, result);

        // 3. 中枢列表（最近 5 个）
        AppendZhongshuSummary(sb, result);

        // 4. 价格概要
        AppendPriceSummary(sb, result);

        var question = QuestionTemplate
            .Replace("{instrument}", instrumentId)
            .Replace("{timeframe}", timeframe);

        return new ContextAssemblyResult
        {
            SystemPrompt = SystemPrompt,
            DataContext = sb.ToString(),
            UserQuestion = question,
            EstimatedTokens = (SystemPrompt.Length + sb.Length + question.Length) / 2
        };
    }

    // ═══════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════

    private static void AppendBiSummary(StringBuilder sb, ChanLunResult r)
    {
        var recent = r.Bis.TakeLast(10).ToList();
        if (recent.Count == 0) { sb.AppendLine("(无笔数据)"); return; }

        sb.AppendLine("## 笔列表（最近 10 笔）");
        sb.AppendLine();
        sb.AppendLine("| # | 方向 | 起(分型价) | 止(分型价) | 最低 | 最高 | 力度 | 涨跌幅 | 起日 | 止日 | K线数 |");
        sb.AppendLine("|---|------|-----------|-----------|------|------|------|--------|------|------|-------|");

        var startIdx = Math.Max(0, r.Bis.Count - 10);
        for (int i = 0; i < recent.Count; i++)
        {
            var bi = recent[i];
            var dir = bi.Type == Direction.Up ? "↑" : "↓";
            sb.AppendLine($"| {startIdx + i + 1} | {dir} | {bi.StartFx.Price:F2} | {bi.EndFx.Price:F2} | {bi.Low:F2} | {bi.High:F2} | {bi.Power:F1} | {bi.ChangePct:F2}% | {bi.DtStart:MM-dd HH:mm} | {bi.DtEnd:MM-dd HH:mm} | {bi.BarCount} |");
        }
        sb.AppendLine();

        // 背驰提示
        if (recent.Count >= 4)
        {
            var lastUp = recent.Where(b => b.Type == Direction.Up).TakeLast(2).ToList();
            var lastDown = recent.Where(b => b.Type == Direction.Down).TakeLast(2).ToList();

            if (lastUp.Count == 2)
            {
                var forceRatio = lastUp[1].Power / Math.Max(lastUp[0].Power, 0.01);
                if (forceRatio < 0.7)
                    sb.AppendLine($"⚠️ **上涨力度衰减**: 最近上涨笔力度 ({lastUp[1].Power:F1}) 不足前一笔 ({lastUp[0].Power:F1}) 的 70%，可能形成**顶背驰**。");
                else if (forceRatio > 1.5)
                    sb.AppendLine($"📈 **上涨加速**: 最近上涨笔力度 ({lastUp[1].Power:F1}) 显著超过前一笔 ({lastUp[0].Power:F1})，趋势强化中。");
            }

            if (lastDown.Count == 2)
            {
                var forceRatio = lastDown[1].Power / Math.Max(lastDown[0].Power, 0.01);
                if (forceRatio < 0.7)
                    sb.AppendLine($"⚠️ **下跌力度衰减**: 最近下跌笔力度 ({lastDown[1].Power:F1}) 不足前一笔 ({lastDown[0].Power:F1}) 的 70%，可能形成**底背驰**。");
                else if (forceRatio > 1.5)
                    sb.AppendLine($"📉 **下跌加速**: 最近下跌笔力度 ({lastDown[1].Power:F1}) 显著超过前一笔 ({lastDown[0].Power:F1})，趋势强化中。");
            }
        }
        sb.AppendLine();
    }

    private static void AppendZhongshuSummary(StringBuilder sb, ChanLunResult r)
    {
        var recent = r.Zhongshus.TakeLast(5).ToList();
        if (recent.Count == 0) { sb.AppendLine("(无中枢数据)"); sb.AppendLine(); return; }

        sb.AppendLine("## 中枢列表（最近 5 个）");
        sb.AppendLine();
        sb.AppendLine("| # | 上沿(Zg) | 下沿(Zd) | 中轨(Zz) | 宽度 | 笔数 | 级别 |");
        sb.AppendLine("|---|---------|---------|---------|------|------|------|");

        var startIdx = Math.Max(0, r.Zhongshus.Count - 5);
        for (int i = 0; i < recent.Count; i++)
        {
            var zs = recent[i];
            var width = zs.Zg - zs.Zd;
            var widthPct = zs.Zz > 0 ? width / zs.Zz * 100 : 0;
            sb.AppendLine($"| {startIdx + i + 1} | {zs.Zg:F2} | {zs.Zd:F2} | {zs.Zz:F2} | {width:F2} ({widthPct:F2}%) | {zs.BiCount} | {zs.Level} |");
        }
        sb.AppendLine();

        // 价格与中枢的关系
        if (recent.Count > 0 && r.StdBars.Count > 0)
        {
            var lastZs = recent[^1];
            var lastPrice = r.StdBars[^1].Close;
            if (lastPrice > lastZs.Zg)
                sb.AppendLine($"📍 当前价格 {lastPrice:F2} 在最新中枢**上方** (Zg={lastZs.Zg:F2})，中枢为支撑。");
            else if (lastPrice < lastZs.Zd)
                sb.AppendLine($"📍 当前价格 {lastPrice:F2} 在最新中枢**下方** (Zd={lastZs.Zd:F2})，中枢为压力。");
            else
                sb.AppendLine($"📍 当前价格 {lastPrice:F2} 在最新中枢**内部** ({lastZs.Zd:F2}~{lastZs.Zg:F2})，处于震荡。");
        }
        sb.AppendLine();
    }

    private static void AppendPriceSummary(StringBuilder sb, ChanLunResult r)
    {
        if (r.StdBars.Count < 2) return;

        var last = r.StdBars[^1];
        var prev = r.StdBars[^2];
        var high20 = r.StdBars.TakeLast(20).Max(b => b.High);
        var low20 = r.StdBars.TakeLast(20).Min(b => b.Low);

        sb.AppendLine("## 价格概要");
        sb.AppendLine();
        sb.AppendLine($"- 最新价: {last.Close:F2} ({last.Dt:yyyy-MM-dd HH:mm})");
        sb.AppendLine($"- 20周期最高: {high20:F2}");
        sb.AppendLine($"- 20周期最低: {low20:F2}");
        sb.AppendLine($"- 最新K线: O={last.Open:F2} H={last.High:F2} L={last.Low:F2} C={last.Close:F2} V={last.Volume:F0}");

        // 价格位置
        var range = high20 - low20;
        if (range > 0)
        {
            var posPct = (last.Close - low20) / range * 100;
            var posLabel = posPct > 70 ? "高位" : posPct < 30 ? "低位" : "中位";
            sb.AppendLine($"- 20周期位置: {posPct:F0}% ({posLabel})");
        }
        sb.AppendLine();
    }

    private static string TranslateTrend(string trend) => trend switch
    {
        "UP" => "上涨 (UP)",
        "DOWN" => "下跌 (DOWN)",
        "CONSOLIDATION" => "震荡 (CONSOLIDATION)",
        _ => "未分类 (UNCLASSIFIED)"
    };
}
