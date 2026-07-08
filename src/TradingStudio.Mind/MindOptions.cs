namespace TradingStudio.Mind;

/// <summary>
/// Mind LLM 配置 — 从 appsettings.json "Mind" section 绑定。
/// </summary>
public class MindOptions
{
    /// <summary>LLM 提供商: "Anthropic" | "OpenAI"</summary>
    public string Provider { get; set; } = "Anthropic";

    /// <summary>默认模型 ID</summary>
    public string DefaultModel { get; set; } = "claude-sonnet-5";

    /// <summary>每次请求最大输出 token 数</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>温度 (0-1)，分析类任务建议 0.1-0.3</summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>Anthropic API Key (优先级: 环境变量 ANTHROPIC_API_KEY > appsettings)</summary>
    public string AnthropicApiKey { get; set; } = "";

    /// <summary>Anthropic API 地址</summary>
    public string AnthropicBaseUrl { get; set; } = "https://api.anthropic.com/v1";

    /// <summary>OpenAI API Key (优先级: 环境变量 OPENAI_API_KEY > appsettings)</summary>
    public string OpenAiApiKey { get; set; } = "";

    /// <summary>OpenAI API 地址（也适用于兼容 API，如 DeepSeek）</summary>
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>验证必填字段</summary>
    public void Validate()
    {
        switch (Provider.ToLowerInvariant())
        {
            case "anthropic":
                if (string.IsNullOrWhiteSpace(AnthropicApiKey))
                    throw new InvalidOperationException(
                        "AnthropicApiKey is required. Set ANTHROPIC_API_KEY env var or configure Mind:AnthropicApiKey.");
                break;
            case "openai":
                if (string.IsNullOrWhiteSpace(OpenAiApiKey))
                    throw new InvalidOperationException(
                        "OpenAiApiKey is required. Set OPENAI_API_KEY env var or configure Mind:OpenAiApiKey.");
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown LLM provider: '{Provider}'. Supported: 'Anthropic', 'OpenAI'.");
        }
    }
}
