using Microsoft.Extensions.Logging;
using TradingStudio.Mind.Clients;
using TradingStudio.Mind.Prompts;
using TradingStudio.Strategy.ChanLun;

namespace TradingStudio.Mind.Analysts;

/// <summary>
/// 缠论分析器 — 将 ChanLunResult 交给 LLM，解读结构并输出交易建议。
/// </summary>
public class ChanLunAnalyst
{
    private readonly ILLMClient _client;
    private readonly ChanLunContextAssembler _assembler;
    private readonly ILogger<ChanLunAnalyst>? _logger;

    public ChanLunAnalyst(
        ILLMClient client,
        ChanLunContextAssembler assembler,
        ILogger<ChanLunAnalyst>? logger = null)
    {
        _client = client;
        _assembler = assembler;
        _logger = logger;
    }

    /// <summary>
    /// 分析缠论结构，返回 LLM 解读的 Markdown 报告。
    /// </summary>
    /// <param name="result">缠论分析结果</param>
    /// <param name="instrumentId">品种代码，如 "SA000"</param>
    /// <param name="timeframe">时间框架，如 "30min"</param>
    public async Task<ChanLunAnalysisResult> AnalyzeAsync(
        ChanLunResult result,
        string instrumentId,
        string timeframe,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        _logger?.LogInformation("Analyzing ChanLun structure: {Instrument} {Timeframe} ({BiCount} bis, {ZsCount} zhongshus, trend={Trend})",
            instrumentId, timeframe, result.BiCount, result.ZhongshuCount, result.Trend);

        // 1. 快速检查：数据是否足够
        if (result.StdCount < 10)
        {
            return new ChanLunAnalysisResult
            {
                InstrumentId = instrumentId,
                Timeframe = timeframe,
                MarkdownReport = $"## {instrumentId} {timeframe} 缠论分析\n\n**数据不足**：仅有 {result.StdCount} 根标准K线，至少需要 10 根。无法进行有意义的缠论分析。",
                IsActionable = false
            };
        }

        // 2. 组装上下文
        var ctx = _assembler.Assemble((result, instrumentId, timeframe), new ContextBudget
        {
            MaxInputTokens = 40_000  // 缠论结构文本不大
        });

        // 3. 构建消息
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(ctx.SystemPrompt),
            ChatMessage.User(ctx.DataContext + "\n\n" + ctx.UserQuestion)
        };

        // 4. 调用 LLM
        var markdown = await _client.ChatAsync(messages, ct: ct);

        // 5. 解析是否可操作
        var isActionable = !markdown.Contains("不建议") && !markdown.Contains("观望");

        return new ChanLunAnalysisResult
        {
            InstrumentId = instrumentId,
            Timeframe = timeframe,
            MarkdownReport = markdown,
            IsActionable = isActionable
        };
    }
}

/// <summary>缠论分析结果</summary>
public record ChanLunAnalysisResult
{
    public string InstrumentId { get; init; } = "";
    public string Timeframe { get; init; } = "";
    public string MarkdownReport { get; init; } = "";
    public bool IsActionable { get; init; }
}
