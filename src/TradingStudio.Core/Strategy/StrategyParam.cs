namespace TradingStudio.Core.Strategy;

/// <summary>
/// 强类型策略参数 — 借鉴 StockSharp StrategyParam&lt;T&gt;。
/// 自带分组、验证、优化范围，编译期类型安全。
///
/// 用法:
///   public StrategyParam&lt;int&gt; FastPeriod { get; } = new("FastPeriod", 5) { Group = "Entry", OptimizeRange = (3, 20, 1) };
///
/// 兼容: 旧的 [StrategyParameter] 属性继续可用，StrategyFactory 同时支持两种参数模式。
/// </summary>
public class StrategyParam<T>
{
    /// <summary>参数名（对应 JSON 配置中的 key）</summary>
    public string Name { get; }

    /// <summary>当前值</summary>
    public T Value { get; set; }

    /// <summary>分组（Entry / Exit / Risk / Position / Filter）</summary>
    public string Group { get; init; } = "General";

    /// <summary>参数说明</summary>
    public string Description { get; init; } = "";

    /// <summary>优化扫描范围 (Min, Max, Step)。null = 不参与优化。</summary>
    public (T Min, T Max, T Step)? OptimizeRange { get; init; }

    /// <summary>自定义验证 (返回 false = 无效值)</summary>
    public Func<T, bool>? Validator { get; init; }

    public StrategyParam(string name, T defaultValue)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Value = defaultValue;
    }

    /// <summary>隐式转换: StrategyParam&lt;T&gt; → T（策略内直接用参数值）</summary>
    public static implicit operator T(StrategyParam<T> p) => p.Value;

    /// <summary>返回 Value 的字符串表示（兼容旧代码中参数 .ToString() 模式）</summary>
    public override string ToString() => Value?.ToString() ?? "";
}
