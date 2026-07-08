using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TradingStudio.Core.Strategy;
using TradingStudio.Engine.Statistics;
using TradingStudio.Mind.Clients;
using TradingStudio.Mind.Prompts;

namespace TradingStudio.Mind.Analysts;

/// <summary>
/// 回测分析师 — 编排完整分析流程：组装上下文 → 调用 LLM → 解析结果。
/// 依赖注入: ILLMClient + BacktestContextAssembler + ILogger
/// </summary>
public class BacktestAnalyst
{
    private readonly ILLMClient _client;
    private readonly BacktestContextAssembler _assembler;
    private readonly ILogger<BacktestAnalyst>? _logger;

    public BacktestAnalyst(
        ILLMClient client,
        BacktestContextAssembler assembler,
        ILogger<BacktestAnalyst>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
        _logger = logger;
    }

    /// <summary>
    /// 分析单策略回测报告 — 返回 Markdown 报告 + 可选结构化诊断。
    /// </summary>
    public async Task<BacktestAnalysisResult> AnalyzeAsync(
        PerformanceReport report,
        StrategyConfig config,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(config);

        _logger?.LogInformation("Analyzing {StrategyId} ({InstrumentCount} instruments, {TradeCount} trades)...",
            report.StrategyId, config.Instruments.Count, report.TotalTrades);

        // 1. 组装上下文
        var ctx = _assembler.Assemble((report, config), new ContextBudget());

        _logger?.LogDebug("Context assembled: ~{Tokens} estimated tokens", ctx.EstimatedTokens);

        // 2. 构建消息列表
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(ctx.SystemPrompt),
            ChatMessage.User(ctx.DataContext + "\n\n" + ctx.UserQuestion)
        };

        // 3. 调用 LLM
        var markdown = await _client.ChatAsync(messages, ct: ct);

        // 4. 尝试解析结构化诊断（best-effort，解析失败不阻断）
        var diagnosis = TryParseDiagnosis(markdown);

        if (diagnosis != null)
        {
            _logger?.LogDebug("Structured diagnosis parsed: {StrengthCount} strengths, {WeaknessCount} weaknesses, {SuggestionCount} suggestions, risk={Risk}",
                diagnosis.Strengths.Count, diagnosis.Weaknesses.Count, diagnosis.Suggestions.Count, diagnosis.RiskLevel);
        }

        return new BacktestAnalysisResult
        {
            StrategyId = report.StrategyId,
            MarkdownReport = markdown,
            Diagnosis = diagnosis
        };
    }

    // ═══════════════════════════════════════════
    // Markdown 解析 — 从 LLM 回复中提取结构化诊断
    // ═══════════════════════════════════════════

    /// <summary>
    /// 从 Markdown 中提取结构化诊断。
    /// 期望格式:
    ///   ## 优势
    ///   - 第一条优势
    ///   - 第二条优势
    ///   ## 缺陷
    ///   - ...
    ///   ## 建议
    ///   - ...
    /// 或中文全角 ##
    /// </summary>
    private static BacktestDiagnosis? TryParseDiagnosis(string markdown)
    {
        try
        {
            var overall = ExtractOverallAssessment(markdown);
            var strengths = ExtractBulletSection(markdown, "优势");
            var weaknesses = ExtractBulletSection(markdown, "缺陷");
            var suggestions = ExtractBulletSection(markdown, "建议");
            var riskLevel = ExtractRiskLevel(markdown);

            // 至少要有一个 section 有内容，才算解析成功
            if (strengths.Count == 0 && weaknesses.Count == 0 && suggestions.Count == 0)
                return null;

            return new BacktestDiagnosis
            {
                OverallAssessment = overall,
                Strengths = strengths,
                Weaknesses = weaknesses,
                Suggestions = suggestions,
                RiskLevel = riskLevel
            };
        }
        catch
        {
            return null; // 解析失败，不影响 Markdown 输出
        }
    }

    private static string ExtractOverallAssessment(string markdown)
    {
        // 找 "综合评价" 或开头的第一句评价
        var match = Regex.Match(markdown,
            @"(?:综合评价|总体评价)[：:]\s*(.+?)(?:\n|$)");
        if (match.Success)
            return match.Groups[1].Value.Trim();

        // Fallback: 取 "【" 包裹的内容
        match = Regex.Match(markdown, @"【(.+?)】");
        if (match.Success)
            return match.Groups[1].Value.Trim();

        return "";
    }

    private static List<string> ExtractBulletSection(string markdown, string sectionName)
    {
        var results = new List<string>();

        // 匹配 "## 优势" 或 "## 优势" 下面的列表项
        // 支持: - item, * item, 1. item, • item
        var pattern = $@"##\s*{sectionName}\s*\n(.*?)(?=##\s|\Z)";
        var match = Regex.Match(markdown, pattern, RegexOptions.Singleline);
        if (!match.Success) return results;

        var section = match.Groups[1].Value;

        // 提取所有列表项
        var bulletPattern = @"(?:^|\n)\s*(?:[-*•]|\d+\.)\s+(.+?)(?=\n\s*(?:[-*•]|\d+\.)|\n\s*\n|\Z)";
        var bullets = Regex.Matches(section, bulletPattern, RegexOptions.Singleline);

        foreach (Match b in bullets)
        {
            var item = b.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(item) && item.Length > 5)
                results.Add(item);
        }

        return results;
    }

    private static string ExtractRiskLevel(string markdown)
    {
        // 找 "风险等级" 或 "风险"
        var match = Regex.Match(markdown,
            @"风险(?:等级|评估)[：:]\s*(.+?)(?:\n|$)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var level = match.Groups[1].Value.Trim();
            if (level.Contains("低")) return "低";
            if (level.Contains("中")) return "中";
            if (level.Contains("高")) return "高";
        }

        // Fallback: "**风险等级**：中"
        match = Regex.Match(markdown, @"\*\*风险[等级评估]*\*\*[：:]\s*(.+?)(?:\n|\*\*)");
        if (match.Success)
        {
            var level = match.Groups[1].Value.Trim();
            if (level.Contains("低")) return "低";
            if (level.Contains("中")) return "中";
            if (level.Contains("高")) return "高";
        }

        return "";
    }
}
