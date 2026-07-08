using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TradingStudio.Mind.Clients;

/// <summary>
/// Anthropic Messages API 客户端 — 直接调 HTTP API，不依赖第三方 SDK。
/// API 文档: https://docs.anthropic.com/en/api/messages
/// </summary>
public class AnthropicClient : ILLMClient
{
    private readonly HttpClient _http;
    private readonly MindOptions _options;
    private readonly ILogger<AnthropicClient>? _logger;

    private const string AnthropicVersion = "2023-06-01";

    public AnthropicClient(MindOptions options, ILogger<AnthropicClient>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3),
            BaseAddress = new Uri(options.AnthropicBaseUrl.TrimEnd('/') + "/")
        };
    }

    public async Task<string> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? chatOpts = null,
        CancellationToken ct = default)
    {
        var body = BuildRequestBody(messages, chatOpts);
        var json = JsonSerializer.Serialize(body, MindJsonContext.Options);

        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "messages")
                {
                    Headers =
                    {
                        { "x-api-key", _options.AnthropicApiKey },
                        { "anthropic-version", AnthropicVersion }
                    },
                    Content = new StringContent(json, System.Text.Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"))
                };

                var response = await _http.SendAsync(request, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    return ParseResponse(responseBody);
                }

                // 401 = API Key 无效，重试无意义
                if ((int)response.StatusCode == 401)
                {
                    var err = TryExtractError(responseBody);
                    throw new UnauthorizedAccessException(
                        $"Anthropic API Key 无效 (401): {err}. 请检查 ANTHROPIC_API_KEY 环境变量或 appsettings.local.json 中的 Mind:AnthropicApiKey。");
                }

                // 429 / 5xx = 可重试
                if (attempt < 3 && ((int)response.StatusCode is 429 or >= 500))
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    _logger?.LogWarning(
                        "Anthropic API {Status} (attempt {Attempt}/3), retrying in {Delay}s...",
                        (int)response.StatusCode, attempt, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    continue;
                }

                response.EnsureSuccessStatusCode();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 超时
                if (attempt < 3)
                {
                    _logger?.LogWarning("Anthropic API timeout (attempt {Attempt}/3), retrying...", attempt);
                    continue;
                }
                throw;
            }
        }
    }

    // ═══════════════════════════════════════════
    // Private helpers
    // ═══════════════════════════════════════════

    private object BuildRequestBody(IReadOnlyList<ChatMessage> messages, ChatOptions? opts)
    {
        // Anthropic: system prompt 走顶层 "system" 字段, 不是 messages[0]
        var systemContent = messages
            .Where(m => m.Role == "system")
            .Select(m => new { type = "text", text = m.Content })
            .ToList<object>();

        var chatMessages = messages
            .Where(m => m.Role != "system")
            .Select(m => new { role = m.Role, content = m.Content })
            .ToList<object>();

        var body = new Dictionary<string, object>
        {
            ["model"] = opts?.Model ?? _options.DefaultModel,
            ["max_tokens"] = opts?.MaxTokens ?? _options.MaxTokens,
            ["temperature"] = opts?.Temperature ?? _options.Temperature,
            ["messages"] = chatMessages
        };

        if (systemContent.Count > 0)
            body["system"] = systemContent;

        return body;
    }

    private static string ParseResponse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        // Anthropic 响应: { "content": [ { "type": "text", "text": "..." } ], ... }
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                    block.TryGetProperty("text", out var text))
                {
                    texts.Add(text.GetString() ?? "");
                }
            }
            return string.Join("\n", texts);
        }

        throw new InvalidOperationException($"Unexpected Anthropic response format: {responseBody[..Math.Min(200, responseBody.Length)]}");
    }

    private static string TryExtractError(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var msg))
                return msg.GetString() ?? responseBody;
        }
        catch { }
        return responseBody[..Math.Min(200, responseBody.Length)];
    }
}

/// <summary>
/// System.Text.Json 序列化选项。使用默认策略（Dictionary key 原样输出，嵌套对象默认 camelCase）。
/// </summary>
internal static class MindJsonContext
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
