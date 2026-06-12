namespace TradingStudio.Options;

/// <summary>行情采集配置 — 从 appsettings.json 绑定</summary>
public class CollectOptions
{
    public const string Section = "Collect";

    public string MdFront { get; init; } = "tcp://182.254.243.31:30011";
    public string BrokerId { get; init; } = "9999";
    public string UserId { get; init; } = "";
    public string Password { get; init; } = "";
    public string SymbolsPath { get; init; } = "symbols.json";
    public string Database { get; init; } = "bars.db";
    public string TickData { get; init; } = "TickData";

    // 命令行过滤（非 JSON 配置）
    public string? ExchangeFilter { get; set; }   // "SHFE" → 仅上期所
    public string? SymbolFilter { get; set; }     // "ag2608" → 仅该合约，"ag" → ag品种下所有
}
