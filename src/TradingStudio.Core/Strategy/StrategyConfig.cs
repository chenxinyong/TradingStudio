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
