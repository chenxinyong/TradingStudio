using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;
using TradingStudio.Data.Import;
using TradingStudio.Data.Storage;

namespace TradingStudio.ToolBox;

/// <summary>
/// 导入管线服务注册扩展。
/// ImportTool 和 JinshuyuanTool 共享相同的基础设施，
/// 通过此扩展方法避免重复代码。
/// </summary>
public static class ImportServiceExtensions
{
    public static IServiceCollection AddImportPipeline(
        this IServiceCollection services, IConfiguration config)
    {
        var dbPath = config["Import:Database"] ?? config["db"] ?? "bars.db";

        // 数据库（Singleton：连接可复用，BarStore 线程安全）
        services.AddSingleton(new BarStore(dbPath));

        // 品种注册表（Singleton：只读数据，全局共享）
        services.AddSingleton(sp =>
        {
            var symbolsPath = FindSymbolsJson();
            return FutureRegistry.Load(symbolsPath);
        });

        // 注意：BarAggregator / DailyBarAggregator 是有状态的，
        // 各 Tool 在 ExecuteAsync 中自行 new + using，不通过 DI 解析。

        return services;
    }

    private static string FindSymbolsJson()
    {
        var exeDir = AppContext.BaseDirectory;
        var path = Path.Combine(exeDir, "symbols.json");
        if (File.Exists(path)) return path;
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), "symbols.json");
        if (File.Exists(cwd)) return cwd;
        var src = Path.Combine(Directory.GetCurrentDirectory(), "..", "TradingStudio", "symbols.json");
        if (File.Exists(src)) return Path.GetFullPath(src);
        throw new FileNotFoundException("symbols.json not found");
    }
}
