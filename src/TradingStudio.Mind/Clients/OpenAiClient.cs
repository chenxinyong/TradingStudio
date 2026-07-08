using Microsoft.Extensions.Logging;

namespace TradingStudio.Mind.Clients;

/// <summary>
/// OpenAI Chat Completions API 客户端（骨架 — Phase 3b 实现）。
/// 也适用于 OpenAI 兼容的 API（DeepSeek, 通义千问, 本地 Ollama 等）。
/// API 文档: https://platform.openai.com/docs/api-reference/chat
/// </summary>
/// <remarks>
/// Phase 3b 实现要点：
///   1. POST {BaseUrl}/chat/completions
///   2. Authorization: Bearer {ApiKey}
///   3. 请求体: { model, messages, max_tokens, temperature }
///   4. 响应体: { choices[0].message.content }
///   5. System prompt 作为 messages[0] role="system"，不是顶层字段
///   6. 与 AnthropicClient 共享相同的重试逻辑（401×, 429/5xx 3次退避）
/// </remarks>
public class OpenAiClient : ILLMClient
{
    private readonly MindOptions _options;
    private readonly ILogger<OpenAiClient>? _logger;

    public OpenAiClient(MindOptions options, ILogger<OpenAiClient>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public Task<string> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        // Phase 3b: 实现 OpenAI Chat Completions API 调用
        // 骨架：抛出清晰的不支持消息，提供迁移路径
        throw new NotSupportedException(
            "OpenAI client is not yet implemented. " +
            "Set Mind.Provider to 'Anthropic' in appsettings.json to use the available client. " +
            "OpenAI support is planned for Phase 3b.");
    }
}
