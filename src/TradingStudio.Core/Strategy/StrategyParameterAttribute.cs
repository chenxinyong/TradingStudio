namespace TradingStudio.Core.Strategy;

/// <summary>
/// 策略参数 — 策略类上的属性标记，引擎自动发现和验证。
/// 策略的自描述机制：参数定义在代码中，配置只需覆盖值。
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class StrategyParameterAttribute : Attribute
{
    public string Description { get; init; } = "";
    public object? DefaultValue { get; init; }
    public double Min { get; init; } = double.MinValue;
    public double Max { get; init; } = double.MaxValue;
    public bool Required { get; init; }
    public string Category { get; init; } = "General";  // Entry / Exit / Risk / Position
}
