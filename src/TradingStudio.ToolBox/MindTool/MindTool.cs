using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingStudio.Core.Strategy;
using TradingStudio.Engine;
using TradingStudio.Engine.Statistics;
using TradingStudio.Mind;
using TradingStudio.Mind.Analysts;
using TradingStudio.Mind.Clients;
using TradingStudio.Mind.Prompts;

namespace TradingStudio.ToolBox.MindTool;

/// <summary>
/// LLM 策略分析工具 — 读取回测报告，调用 LLM 生成策略诊断。
/// 用法: ToolBox mind --report <report.json> [--config <strategy.json>]
/// </summary>
public class MindTool : IToolCommand
{
    public string Name => "mind";
    public string? Alias => "ai";
    public string Description => "LLM 策略分析 — 读取回测报告，AI 生成策略诊断报告";

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // 1. 绑定 MindOptions — 合并 host config + appsettings.local.json
        var mindConfig = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddConfiguration(config)  // host 已加载的配置 (appsettings.json + env vars + CLI args)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
            .Build();

        var mindOpts = new MindOptions();
        mindConfig.GetSection("Mind").Bind(mindOpts);

        // 2. API Key 优先级: 环境变量 > appsettings.local.json > appsettings.json
        OverrideFromEnvironment(mindOpts);

        services.AddSingleton(mindOpts);

        // 3. 注册 Mind 组件
        services.AddSingleton<ILLMClient>(sp =>
            MindClientFactory.Create(mindOpts, sp.GetService<ILoggerFactory>()));
        services.AddSingleton<BacktestContextAssembler>();
        services.AddSingleton<BacktestAnalyst>();
    }

    public async Task<int> ExecuteAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        // 1. 解析 CLI 参数
        string? reportPath = null, configPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--report" or "-r" && i + 1 < args.Length)
                reportPath = args[++i];
            else if (args[i] is "--config" or "-c" && i + 1 < args.Length)
                configPath = args[++i];
        }

        if (reportPath == null)
        {
            PrintUsage();
            return 1;
        }

        // 2. 验证文件存在
        if (!File.Exists(reportPath))
        {
            Console.Error.WriteLine($"报告文件不存在: {reportPath}");
            return 1;
        }

        // 3. 加载 EngineReport（需要 EquityCurveJsonConverter）
        var reportJson = await File.ReadAllTextAsync(reportPath, ct);
        var jsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new EquityCurveJsonConverter() }
        };
        EngineReport? report;
        try
        {
            report = JsonSerializer.Deserialize<EngineReport>(reportJson, jsonOpts);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"报告 JSON 解析失败: {ex.Message}");
            return 1;
        }

        if (report == null || report.StrategyReports.Count == 0)
        {
            Console.Error.WriteLine("报告中无策略数据 (StrategyReports 为空)。");
            return 1;
        }

        // 4. 加载 StrategyConfig（可选）
        StrategyConfig? strategyConfig = null;
        if (configPath != null && File.Exists(configPath))
        {
            var cfgJson = await File.ReadAllTextAsync(configPath, ct);
            strategyConfig = JsonSerializer.Deserialize<StrategyConfig>(cfgJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        // 5. 验证 API Key
        var mindOpts = sp.GetRequiredService<MindOptions>();
        try
        {
            mindOpts.Validate();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LLM 配置错误: {ex.Message}");
            return 1;
        }

        // 6. 执行分析
        var analyst = sp.GetRequiredService<BacktestAnalyst>();
        var log = sp.GetRequiredService<ILogger<MindTool>>();

        Console.WriteLine($"LLM Provider: {mindOpts.Provider} | Model: {mindOpts.DefaultModel}");
        Console.WriteLine($"Analyzing {report.StrategyReports.Count} strategy(s)...");
        Console.WriteLine();

        var successCount = 0;
        foreach (var perfReport in report.StrategyReports)
        {
            var cfg = strategyConfig ?? new StrategyConfig
            {
                StrategyId = perfReport.StrategyId,
                StrategyType = "(unknown)"
            };

            log.LogInformation("Analyzing {StrategyId} ({TradeCount} trades)...",
                perfReport.StrategyId, perfReport.TotalTrades);

            try
            {
                var result = await analyst.AnalyzeAsync(perfReport, cfg, ct);

                // 7. 写出 Markdown 报告
                var mdPath = Path.ChangeExtension(reportPath,
                    $".{SanitizeFileName(perfReport.StrategyId)}.analysis.md");
                await File.WriteAllTextAsync(mdPath, result.MarkdownReport,
                    System.Text.Encoding.UTF8, ct);

                Console.WriteLine($"  [{perfReport.StrategyId}] → {mdPath}");

                // 8. 写出结构化诊断 JSON（可选）
                if (result.Diagnosis != null)
                {
                    var diagPath = Path.ChangeExtension(reportPath,
                        $".{SanitizeFileName(perfReport.StrategyId)}.analysis.json");
                    var diagJson = JsonSerializer.Serialize(result.Diagnosis,
                        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    await File.WriteAllTextAsync(diagPath, diagJson,
                        System.Text.Encoding.UTF8, ct);
                }

                successCount++;
            }
            catch (OperationCanceledException)
            {
                log.LogWarning("{StrategyId} analysis cancelled", perfReport.StrategyId);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "{StrategyId} analysis failed", perfReport.StrategyId);
                Console.Error.WriteLine($"  [{perfReport.StrategyId}] 分析失败: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"分析完成: {successCount}/{report.StrategyReports.Count} 策略成功");
        return successCount == report.StrategyReports.Count ? 0 : 1;
    }

    // ═══════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════

    private static void OverrideFromEnvironment(MindOptions opts)
    {
        var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(anthropicKey))
            opts.AnthropicApiKey = anthropicKey;

        var anthropicUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        if (!string.IsNullOrWhiteSpace(anthropicUrl))
            opts.AnthropicBaseUrl = anthropicUrl;

        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(openAiKey))
            opts.OpenAiApiKey = openAiKey;

        var openAiUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        if (!string.IsNullOrWhiteSpace(openAiUrl))
            opts.OpenAiBaseUrl = openAiUrl;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "analysis" : sanitized;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("ToolBox mind — LLM 策略分析");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  ToolBox mind --report <report.json> [--config <strategy.json>]");
        Console.WriteLine();
        Console.WriteLine("选项:");
        Console.WriteLine("  --report, -r   回测报告 JSON 文件路径（必需）");
        Console.WriteLine("  --config, -c   策略配置 JSON 文件路径（可选，用于补充策略参数信息）");
        Console.WriteLine();
        Console.WriteLine("输出:");
        Console.WriteLine("  {report}.{StrategyId}.analysis.md   Markdown 分析报告");
        Console.WriteLine("  {report}.{StrategyId}.analysis.json  结构化诊断 JSON");
        Console.WriteLine();
        Console.WriteLine("API Key 配置 (优先级: 环境变量 > appsettings.local.json > appsettings.json):");
        Console.WriteLine("  $env:ANTHROPIC_API_KEY = \"sk-ant-...\"");
        Console.WriteLine("  或 appsettings.local.json: {\"Mind\": {\"AnthropicApiKey\": \"sk-ant-...\"}}");
        Console.WriteLine();
        Console.WriteLine("切换 LLM Provider:");
        Console.WriteLine("  appsettings.json: Mind.Provider = \"Anthropic\" | \"OpenAI\"");
        Console.WriteLine();
        Console.WriteLine("示例:");
        Console.WriteLine("  ToolBox mind -r configs/donchian_rb.report.json");
        Console.WriteLine("  ToolBox mind -r configs/donchian_rb.report.json -c configs/donchian_rb.json");
    }
}
