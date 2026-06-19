using System.Text.Json;

namespace TradingStudio.Core.Models;

/// <summary>
/// 品种注册表 — 从 symbols.json 加载，O(1) 查询。
/// </summary>
public sealed class FutureRegistry
{
    private readonly Dictionary<string, Future> _byCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ExchangeCode, List<Future>> _byExchange = new();

    public IReadOnlyDictionary<string, Future> All => _byCode;
    public int Count => _byCode.Count;

    /// <summary>Top 30 品种快速查找（O(1) HashSet，大小写无关）</summary>
    public HashSet<string> Top30Codes { get; } = new(StringComparer.OrdinalIgnoreCase);

    private FutureRegistry() { }

    public static FutureRegistry Load(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        return Parse(doc.RootElement);
    }

    public static FutureRegistry LoadFromJson(string json) =>
        Parse(JsonDocument.Parse(json).RootElement);

    private static FutureRegistry Parse(JsonElement root)
    {
        var reg = new FutureRegistry();
        foreach (var s in root.GetProperty("symbols").EnumerateArray())
        {
            var p = new Future
            {
                Id             = s.GetProperty("id").GetInt32(),
                Exchange       = Enum.Parse<ExchangeCode>(s.GetProperty("exchange").GetString()!),
                Code           = s.GetProperty("code").GetString()!,
                Name           = s.GetProperty("name").GetString()!,
                Category       = s.GetProperty("category").GetString()!,
                DeliveryType   = s.GetProperty("deliveryType").GetString()!,
                TradingUnit    = s.GetProperty("tradingUnit").GetDecimal(),
                UnitName       = s.GetProperty("unitName").GetString()!,
                TickSize       = s.GetProperty("tickSize").GetDecimal(),
                TickValue      = s.GetProperty("tickValue").GetDecimal(),
                PriceLimitPct  = s.GetProperty("priceLimitPct").GetDecimal(),
                MarginRate     = s.GetProperty("marginRate").GetDecimal(),
                FeeRate        = TryGetDouble(s, "feeRate"),
                CloseTodayFeeRate = TryGetDouble(s, "closeTodayFeeRate"),
                Months         = s.GetProperty("months").GetString()!,
                TradingHours   = TryGet(s, "tradingHours", ""),
                IsTop30        = TryGetBool(s, "isTop30"),
                ContractCycle  = TryGet(s, "contractCycle", ""),
            };
            reg._byCode[p.Code] = p;
            if (p.IsTop30) reg.Top30Codes.Add(p.Code);
            if (!reg._byExchange.TryGetValue(p.Exchange, out var list))
                reg._byExchange[p.Exchange] = list = [];
            list.Add(p);
        }
        return reg;
    }

    // === 查询 ===

    public Future? Find(string code) =>
        _byCode.TryGetValue(code, out var p) ? p : null;

    /// <summary>从 CTP InstrumentID 提取品种代码并查找</summary>
    public Future? Resolve(string instrumentId) =>
        Find(instrumentId.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'));

    public IReadOnlyList<Future> OfExchange(ExchangeCode ex) =>
        _byExchange.GetValueOrDefault(ex) ?? [];

    public IReadOnlyList<Future> OfCategory(string cat) =>
        _byCode.Values.Where(p => p.Category == cat).ToList();

    public IReadOnlyList<string> Categories =>
        _byCode.Values.Select(p => p.Category).Distinct().OrderBy(c => c).ToList();

    public override string ToString() => $"FutureRegistry({Count})";

    private static string TryGet(JsonElement el, string key, string def) =>
        el.TryGetProperty(key, out var v) ? v.GetString() ?? def : def;

    private static double TryGetDouble(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) ? v.GetDouble() : 0;

    private static bool TryGetBool(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
}
