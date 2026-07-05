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
    public string PrimaryBarType { get; init; } = "bars_1min";

    /// <summary>Bar 周期（分钟）: 1=1min, 5/15/30/60=即时聚合。仅回测生效。</summary>
    public int BarPeriodMinutes { get; init; } = 1;

    // ═══ 资金分配 ═══
    public decimal AllocatedCapital { get; init; }
    public decimal MaxDrawdownPct { get; init; } = 0.20m;
    public int MaxPositionPerInstrument { get; init; } = 5;

    // ═══ 回测数据范围与验证模式 ═══
    /// <summary>数据起始日期 (默认: 库中最早)</summary>
    public DateTime? DataStartDate { get; init; }

    /// <summary>数据结束日期 (默认: 库中最晚)</summary>
    public DateTime? DataEndDate { get; init; }

    /// <summary>
    /// 样本内/外分界日期。此日期之前为 IS (开发+参数优化)，之后为 OOS (仅验证一次)。
    /// null 表示不区分 IS/OOS，全量数据参与回测。
    /// </summary>
    public DateTime? OptimizationEndDate { get; init; }

    /// <summary>回测模式: Full(全量), Optimization(仅IS), Validation(仅OOS)</summary>
    public BacktestMode BacktestMode { get; init; } = BacktestMode.Full;

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

/// <summary>强类型参数容器，支持 JSON 反序列化</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(StrategyParametersConverter))]
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

/// <summary>JSON 转换器：将 {"key": value, ...} 转为 StrategyParameters</summary>
public class StrategyParametersConverter : System.Text.Json.Serialization.JsonConverter<StrategyParameters>
{
    public override StrategyParameters? Read(ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartObject)
            throw new System.Text.Json.JsonException("Expected object");

        var parameters = new StrategyParameters();
        while (reader.Read())
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.EndObject)
                return parameters;

            if (reader.TokenType != System.Text.Json.JsonTokenType.PropertyName)
                throw new System.Text.Json.JsonException("Expected property name");

            var name = reader.GetString()!;
            reader.Read();

            // 根据 JSON token 类型推断 CLR 类型
            object value = reader.TokenType switch
            {
                System.Text.Json.JsonTokenType.Number when reader.TryGetInt32(out var i) => i,
                System.Text.Json.JsonTokenType.Number when reader.TryGetDouble(out var d) => d,
                System.Text.Json.JsonTokenType.Number => reader.GetDecimal(),
                System.Text.Json.JsonTokenType.String => reader.GetString()!,
                System.Text.Json.JsonTokenType.True => true,
                System.Text.Json.JsonTokenType.False => false,
                _ => System.Text.Json.JsonSerializer.Deserialize<object>(ref reader, options) ?? ""
            };
            parameters.Add(name, value);
        }

        return parameters;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer,
        StrategyParameters value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in value)
        {
            writer.WritePropertyName(k);
            System.Text.Json.JsonSerializer.Serialize(writer, v, options);
        }
        writer.WriteEndObject();
    }
}

/// <summary>风控规则配置</summary>
public class RiskRuleConfig
{
    public string Type { get; init; } = "";
    public Dictionary<string, object>? Parameters { get; init; }
}

/// <summary>
/// 回测模式 — 控制数据范围以强制执行 IS/OOS 分离，防止过度拟合。
/// </summary>
public enum BacktestMode
{
    /// <summary>全量数据，不区分 IS/OOS（仅限初步探索，正式评估禁止使用）</summary>
    Full,

    /// <summary>仅样本内 (In-Sample)，用于策略开发和参数优化</summary>
    Optimization,

    /// <summary>仅样本外 (Out-of-Sample)，用于最终验证。参数必须来自 Optimization 结果，禁止调参。</summary>
    Validation
}
