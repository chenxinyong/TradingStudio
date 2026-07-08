using Microsoft.Extensions.Logging;

namespace TradingStudio.Mind.Clients;

/// <summary>
/// LLM 客户端工厂 — 根据 MindOptions.Provider 动态创建对应的客户端。
/// 改 appsettings.json 的 Provider 值即可切换，不需要重新编译。
/// </summary>
public static class MindClientFactory
{
    /// <summary>
    /// 创建 LLM 客户端实例。
    /// </summary>
    /// <param name="options">LLM 配置</param>
    /// <param name="loggerFactory">可选的日志工厂</param>
    /// <returns>ILLMClient 实例</returns>
    public static ILLMClient Create(MindOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        return options.Provider.ToLowerInvariant() switch
        {
            "openai" => new OpenAiClient(options, loggerFactory?.CreateLogger<OpenAiClient>()),
            "anthropic" => new AnthropicClient(options, loggerFactory?.CreateLogger<AnthropicClient>()),
            _ => throw new NotSupportedException(
                $"Unsupported LLM provider: '{options.Provider}'. Supported: 'Anthropic', 'OpenAI'.")
        };
    }
}
