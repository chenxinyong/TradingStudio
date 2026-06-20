namespace TradingStudio.Engine;

/// <summary>
/// 引擎统一配置 — 回测和实盘共用，从 CLI 参数 + 策略 JSON 合并生成。
///
/// 数据流:
///   CLI --config → StrategyConfig (品种/策略参数/资金分配)
///   CLI --start/--end → StartTime/EndTime (回测窗口)
///   StrategyConfig.StrategyType → StrategyFactory.Create → IStrategy
///   StrategyConfig.AllocatedCapital → PortfolioManager.CreateSubPortfolio
/// </summary>
public class EngineOptions
{
    /// <summary>回测起始时间（含）。实盘模式下为启动时间。</summary>
    public DateTime StartTime { get; init; }

    /// <summary>回测结束时间（含）。实盘模式下为 MaxValue，持续运行直到取消。</summary>
    public DateTime EndTime { get; init; }

    /// <summary>订阅的品种代码列表（产品级如 "rb"，非合约级如 "rb2608"）。
    /// 合约展开由 DataFeed 负责。</summary>
    public IReadOnlyList<string> Instruments { get; init; } = [];

    /// <summary>策略配置列表 — 每个配置一个独立子账户运行。
    /// 来源: CLI --config 指定的 JSON 文件 (如 configs/donchian_rb_15min.json)。</summary>
    public IReadOnlyList<Core.Strategy.StrategyConfig> StrategyConfigs { get; init; } = [];

    /// <summary>总起始资金（所有策略共享同一个 PortfolioManager 的初始现金池）。</summary>
    public decimal StartingCapital { get; init; }

    /// <summary>Tick 模式下策略下单后延迟几个 Tick 撮合。
    /// 0 = 同 Tick 立即撮合（默认，回测更快）；1+ = 模拟网络延迟 + 交易所处理时间。</summary>
    public int TickFillDelay { get; init; } = 0;

    /// <summary>实盘模式：不生成报告，持续运行直到取消。DataFeed 切换到 CtpLiveFeed。</summary>
    public bool IsLive { get; init; }

    /// <summary>预热天数：启动前加载多少天历史 Bar 喂策略的 Initialize + Warmup 阶段。
    /// 策略在此期间只更新指标/状态（IsWarmup=true），不产生交易信号。
    /// 典型值: 60 (MA60 需要至少 60 根 Bar), 0 = 不预热。</summary>
    public int WarmupDays { get; init; }

    /// <summary>跳过集合竞价 Tick（FlagAuction=1）。集合竞价阶段价格不真实，
    /// 跳过可避免虚假信号触发。集合竞价结束后才产生第一个 Tick。</summary>
    public bool SkipAuction { get; init; } = true;
}
