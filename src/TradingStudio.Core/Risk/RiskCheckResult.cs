namespace TradingStudio.Core.Risk;

/// <summary>风控检查结果</summary>
public record RiskCheckResult
{
    public bool Passed { get; init; } = true;
    public RiskCheckLevel Level { get; init; } = RiskCheckLevel.Pass;
    public string? Reason { get; init; }
    public string? RuleName { get; init; }

    public static RiskCheckResult Pass => new() { Passed = true, Level = RiskCheckLevel.Pass };
    public static RiskCheckResult Warning(string rule, string reason) =>
        new() { Passed = true, Level = RiskCheckLevel.Warning, RuleName = rule, Reason = reason };
    public static RiskCheckResult Reject(string rule, string reason) =>
        new() { Passed = false, Level = RiskCheckLevel.Reject, RuleName = rule, Reason = reason };
}

public enum RiskCheckLevel { Pass, Warning, Reject }
