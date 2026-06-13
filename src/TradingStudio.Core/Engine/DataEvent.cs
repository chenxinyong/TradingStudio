namespace TradingStudio.Core.Engine;

/// <summary>统一数据事件 —— Tick 和 Bar 的共同载体</summary>
public abstract record DataEvent
{
    public DateTimeOffset Time { get; init; }
}

/// <summary>Tick 事件 —— 单笔行情更新</summary>
public sealed record TickEvent : DataEvent
{
    public required TradingStudio.Core.Models.TickRecord Tick { get; init; }
    public required string InstrumentId { get; init; }
    public required DateOnly TradingDay { get; init; }
}

/// <summary>Bar 事件 —— 一根完成的 K 线</summary>
public sealed record BarEvent : DataEvent
{
    public required TradingStudio.Core.Models.Bar Bar { get; init; }

    /// <summary>此 Bar 是否刚刚闭合（true: 新 K 线开始; false: 历史回放中的中间 Bar）</summary>
    public bool IsNewBar { get; init; }
}
