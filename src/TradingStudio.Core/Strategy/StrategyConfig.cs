namespace TradingStudio.Core.Strategy;

/// <summary>完整策略配置 — 从 JSON 加载，验证后不可变</summary>
public class StrategyConfig
{
    // ═══ 标识 ═══
    public string StrategyId { get; init; } = "";
    public string StrategyType { get; init; } = "";
    public string? Description { get; init; }
    public int Version { get; init; } = 1;

    // ═══ 订阅 ═══
    public IReadOnlyList<string> Instruments { get; init; } = [];
    public string PrimaryBarType { get; init; } = "1min";

    // ═══ 资金分配 ═══
    public decimal AllocatedCapital { get; init; }
    public decimal MaxDrawdownPct { get; init; } = 0.20m;
    public int MaxPositionPerInstrument { get; init; } = 5;

    // ═══ 执行优先级（同品种多策略争抢流动性时） ═══
    public int Priority { get; init; } = 0;

    // ═══ 策略参数（类型安全字典） ═══
    public StrategyParameters Parameters { get; init; } = new();

    // ═══ 风控规则 ═══
    public IReadOnlyList<RiskRuleConfig> RiskRules { get; init; } = [];

    // ═══ 调度 ═══
    public string SessionFilter { get; init; } = "All";
    public bool SkipAuction { get; init; } = true;
}

/// <summary>强类型参数容器</summary>
public class StrategyParameters : IEnumerable<KeyValuePair<string, object>>
{
    private readonly Dictionary<string, object> _values = new();

    public T Get<T>(string name) => (T)_values[name];
    public bool TryGet<T>(string name, out T value)
    {
        if (_values.TryGetValue(name, out var obj) && obj is T t) { value = t; return true; }
        value = default!;
        return false;
    }
    public bool Contains(string name) => _values.ContainsKey(name);
    public int Count => _values.Count;

    public void Add(string name, object value) => _values[name] = value;
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>风控规则配置</summary>
public class RiskRuleConfig
{
    public string Type { get; init; } = "";
    public Dictionary<string, object>? Parameters { get; init; }
}
