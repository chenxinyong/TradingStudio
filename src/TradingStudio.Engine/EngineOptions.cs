namespace TradingStudio.Engine;

/// <summary>引擎配置</summary>
public class EngineOptions
{
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public IReadOnlyList<string> Instruments { get; init; } = [];
    public IReadOnlyList<Core.Strategy.StrategyConfig> StrategyConfigs { get; init; } = [];
    public decimal StartingCapital { get; init; }

    /// <summary>Tick 模式下策略下单后延迟几个 Tick 撮合。
    /// 0 = 同 Tick 立即撮合（默认）；1 = 延迟到下一 Tick（更保守）。</summary>
    public int TickFillDelay { get; init; } = 0;

    /// <summary>实盘模式：不生成报告，持续运行直到取消</summary>
    public bool IsLive { get; init; }
}
