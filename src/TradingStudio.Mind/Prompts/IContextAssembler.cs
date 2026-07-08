namespace TradingStudio.Mind.Prompts;

/// <summary>
/// 上下文组装器 — 把 Trading 领域数据压缩成 LLM 上下文窗口装得下的文本。
/// 这是 Mind 中最关键的环节：原始 Bar 数据不能塞进去，需要"先聚合、再送入"。
/// </summary>
public interface IContextAssembler<TInput>
{
    /// <summary>组装 LLM 上下文。返回的字符串应在 token 预算内。</summary>
    ContextAssemblyResult Assemble(TInput input, ContextBudget budget);
}

/// <summary>上下文窗口 token 预算分配</summary>
public record ContextBudget
{
    /// <summary>上下文窗口总 token 数（默认 100K，留 20K 给回复）</summary>
    public int MaxInputTokens { get; init; } = 80_000;

    /// <summary>System prompt 占比</summary>
    public double SystemPromptRatio { get; init; } = 0.05;

    /// <summary>数据部分占比</summary>
    public double DataSectionRatio { get; init; } = 0.70;

    /// <summary>用户问题占比</summary>
    public double QuestionRatio { get; init; } = 0.25;
}

/// <summary>组装完成的 LLM 上下文</summary>
public record ContextAssemblyResult
{
    /// <summary>系统提示词（角色定义 + 分析框架）</summary>
    public string SystemPrompt { get; init; } = "";

    /// <summary>压缩后的交易数据</summary>
    public string DataContext { get; init; } = "";

    /// <summary>用户的分析问题</summary>
    public string UserQuestion { get; init; } = "";

    /// <summary>估算的总 token 数</summary>
    public int EstimatedTokens { get; init; }
}
