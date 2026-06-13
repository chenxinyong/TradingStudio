namespace TradingStudio.Core.Indicators;

/// <summary>
/// 指标接口 — 纯数学变换，无观点。
/// 指标回答"这个数字是多少"，不知道自己在预测什么。
/// </summary>
public interface IIndicator
{
    /// <summary>指标名称（如 "SMA"、"RSI"）</summary>
    string Name { get; }

    /// <summary>参数描述（如 "period=20"），用于 registry key</summary>
    string Tag { get; }

    /// <summary>是否有足够历史数据产出有效值</summary>
    bool IsReady { get; }

    /// <summary>当前值</summary>
    double CurrentValue { get; }

    /// <summary>预热期还差多少根 Bar</summary>
    int WarmupPeriod { get; }

    /// <summary>历史值序列（默认上限 100K，超出环形覆盖最旧值）</summary>
    IReadOnlyList<double> Values { get; }

    /// <summary>喂入新 Bar，更新内部状态</summary>
    void Update(Models.Bar bar);

    /// <summary>重置</summary>
    void Reset();
}
