namespace TradingStudio.Mind.Analysts;

/// <summary>回测分析结果 — Markdown 给人看，Diagnosis 给程序用</summary>
public record BacktestAnalysisResult
{
    /// <summary>策略 ID</summary>
    public string StrategyId { get; init; } = "";

    /// <summary>LLM 生成的 Markdown 分析报告</summary>
    public string MarkdownReport { get; init; } = "";

    /// <summary>结构化诊断（best-effort 从 Markdown 中提取）</summary>
    public BacktestDiagnosis? Diagnosis { get; init; }
}

/// <summary>结构化诊断 — 从 LLM 回复中提取的关键发现</summary>
public record BacktestDiagnosis
{
    /// <summary>综合评价（1-2 句）</summary>
    public string OverallAssessment { get; init; } = "";

    /// <summary>策略优点</summary>
    public List<string> Strengths { get; init; } = [];

    /// <summary>策略缺陷</summary>
    public List<string> Weaknesses { get; init; } = [];

    /// <summary>优化建议</summary>
    public List<string> Suggestions { get; init; } = [];

    /// <summary>风险等级: "低" / "中" / "高"</summary>
    public string RiskLevel { get; init; } = "";
}
