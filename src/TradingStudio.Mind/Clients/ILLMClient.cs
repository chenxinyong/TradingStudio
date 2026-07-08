namespace TradingStudio.Mind.Clients;

/// <summary>
/// LLM 客户端最小抽象 — 一个 ChatAsync 方法覆盖所有分析场景。
/// 不依赖任何第三方 AI SDK，直接走 HTTP API。
/// </summary>
public interface ILLMClient
{
    /// <summary>
    /// 发送消息列表，返回 LLM 文本回复。
    /// messages[0] 通常是 system，后续是 user/assistant 交替。
    /// </summary>
    Task<string> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default);
}

/// <summary>对话消息</summary>
public record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
    public static ChatMessage Assistant(string content) => new("assistant", content);
}

/// <summary>每次调用可覆盖的参数</summary>
public record ChatOptions
{
    /// <summary>覆盖默认模型</summary>
    public string? Model { get; init; }

    /// <summary>最大输出 token 数</summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>温度 (0-1)</summary>
    public double Temperature { get; init; } = 0.3;
}
