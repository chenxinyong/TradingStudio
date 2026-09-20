using System.Data.Common;
using System.Globalization;
using DuckDB.NET.Data;

namespace TradingStudio.Data.Factors;

/// <summary>
/// Carry（展期收益率）因子面板构建器。
///
/// 因子定义（与 docs/trading/因子研究/Carry反转因子深度分析.md 一致）:
///   Carry = (近月收盘 − 次近月收盘) / 次近月收盘
///   负值 = 升水(contango)，正值 = 贴水(backwardation)。
///
/// 口径（三口径决策已定）:
///   分母   = 次近月（弃用 roll_yield 的 /近月）
///   方向   = 做多低 Carry（深升水）、做空高 Carry（深贴水）＝ Q1−Q5（反转）
///   近月   = skip=1：合约代码解析，交割月严格晚于当月
///
/// 数据边界:
///   - 单月合约只在 bars_day / bars_1min 两张表，Carry 从 bars_day 现算。
///   - 连续合约 xxx000 是裸拼接、无复权 → 只能用于收益面板，不能反推展期收益。
///   - CFFEX 股指/国债无单月合约 → Carry 只覆盖商品期货。
/// </summary>
public static class CarryPanelBuilder
{
    public const double PriceDiv = 10_000_000.0;

    /// <summary>流动性品种白名单（db 小写码），覆盖 7 大板块，与 carry_csmom_factor.py UNIVERSE 一致。</summary>
    public static readonly IReadOnlySet<string> Universe = new HashSet<string>(StringComparer.Ordinal)
    {
        "au", "ag", "cu", "al", "zn", "ni", "sn", "pb",
        "sc", "fu", "lu", "bu",
        "ma", "ta", "sa", "fg", "eg", "ur",
        "ru", "v", "l", "pp", "eb",
        "rb", "i", "jm", "j", "hc",
        "m", "rm", "y", "oi", "p", "a",
        "sr", "cf",
        "lc", "si",
        "sp", "ss", "pg", "ec", "ao",
        "br", "nr", "bc",
    };

    /// <summary>从 bars_day 现算 Carry 面板与连续合约收盘面板。</summary>
    public static async Task<CarryPanelData> BuildAsync(string dbPath)
    {
        // 连续合约收盘面板: day → product → close（用于收益面板）
        var closeByDay = new Dictionary<DateTime, Dictionary<string, double>>();
        // 单月合约明细: (product, day, deliveryYm, close)
        var individual = new List<(string Product, DateTime Day, int DeliveryYm, double Close)>();

        using var conn = new DuckDBConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        // ── 连续合约 xxx000 ──
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT instrument_id, trading_day, close FROM bars_day WHERE LOWER(instrument_id) LIKE '%000'";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var iid = reader.GetString(0);
                var day = ReadDay(reader, 1);
                var close = reader.GetInt64(2) / PriceDiv;
                if (day == default) continue;
                var prod = iid.ToLowerInvariant().Replace("000", "");
                if (!Universe.Contains(prod)) continue;
                if (!closeByDay.TryGetValue(day, out var m)) { m = new(); closeByDay[day] = m; }
                m[prod] = close;
            }
        }

        // ── 单月合约 ──
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT instrument_id, trading_day, close FROM bars_day WHERE LOWER(instrument_id) NOT LIKE '%000'";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var iid = reader.GetString(0);
                var day = ReadDay(reader, 1);
                var close = reader.GetInt64(2) / PriceDiv;
                if (day == default) continue;
                var (prod, ym) = ParseContract(iid);
                if (prod.Length == 0 || ym < 0 || !Universe.Contains(prod)) continue;
                individual.Add((prod, day, ym, close));
            }
        }

        var carryByDay = ComputeCarry(individual);
        var products = closeByDay.Values.SelectMany(m => m.Keys).Distinct()
            .OrderBy(p => p, StringComparer.Ordinal).ToList();

        return new CarryPanelData
        {
            Carry = carryByDay,
            ContinuousClose = closeByDay,
            Products = products,
        };
    }

    /// <summary>
    /// 每 (product, trading_day) 取交割月晚于当月的最近两个合约，carry = (近月−次月)/次月。
    /// </summary>
    private static Dictionary<DateTime, Dictionary<string, double>> ComputeCarry(
        List<(string Product, DateTime Day, int DeliveryYm, double Close)> individual)
    {
        var grouped = new Dictionary<(string Product, DateTime Day), List<(int DeliveryYm, double Close)>>();
        foreach (var (prod, day, ym, close) in individual)
        {
            var curYm = day.Year * 12 + day.Month;
            if (ym <= curYm) continue;   // 排除当月及之前交割的合约
            if (!grouped.TryGetValue((prod, day), out var list)) { list = new(); grouped[(prod, day)] = list; }
            list.Add((ym, close));
        }

        var carry = new Dictionary<DateTime, Dictionary<string, double>>();
        foreach (var ((prod, day), list) in grouped)
        {
            if (list.Count < 2) continue;
            list.Sort((a, b) => a.DeliveryYm.CompareTo(b.DeliveryYm));
            var near = list[0].Close;   // 交割月最近
            var nxt = list[1].Close;    // 次近
            if (near <= 0 || nxt <= 0) continue;
            var c = (near - nxt) / nxt;
            if (!carry.TryGetValue(day, out var m)) { m = new(); carry[day] = m; }
            m[prod] = c;
        }
        return carry;
    }

    /// <summary>
    /// 解析 instrument_id → (product 小写, deliveryYm=年*12+月)。
    /// 4 位数字 YYMM（SHFE/DCE/INE/GFEX）; 3 位数字 YMM（CZCE）。
    /// 字母后跟非数字（如 sc2609TAS）→ 返回无效。
    /// </summary>
    public static (string Product, int DeliveryYm) ParseContract(string instrumentId)
    {
        int i = 0;
        while (i < instrumentId.Length && char.IsLetter(instrumentId[i])) i++;
        if (i == 0 || i == instrumentId.Length) return ("", -1);
        for (int j = i; j < instrumentId.Length; j++)
            if (!char.IsDigit(instrumentId[j])) return ("", -1);

        var prod = instrumentId[..i].ToLowerInvariant();
        var digits = instrumentId[i..];
        if (digits.Length == 4)
        {
            var yy = int.Parse(digits.AsSpan(0, 2));
            var mm = int.Parse(digits.AsSpan(2, 2));
            return (prod, (2000 + yy) * 12 + mm);
        }
        if (digits.Length == 3)
        {
            var y = int.Parse(digits.AsSpan(0, 1));
            var mm = int.Parse(digits.AsSpan(1, 2));
            return (prod, (2020 + y) * 12 + mm);
        }
        return (prod, -1);
    }

    private static DateTime ReadDay(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return default;
        try
        {
            // bars_day 的 trading_day 可能是 DATE 或 VARCHAR（跨历史库版本）
            if (reader.GetFieldType(ordinal) == typeof(string))
                return DateTime.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture).Date;
            return reader.GetDateTime(ordinal).Date;
        }
        catch { return default; }
    }
}

/// <summary>Carry 面板构建结果。</summary>
public sealed class CarryPanelData
{
    /// <summary>Carry 面板: trading_day → product → carry 值。</summary>
    public required Dictionary<DateTime, Dictionary<string, double>> Carry { get; init; }

    /// <summary>连续合约收盘面板: trading_day → product → close（×1 元，已除 1e7）。</summary>
    public required Dictionary<DateTime, Dictionary<string, double>> ContinuousClose { get; init; }

    /// <summary>有序品种列表（连续合约出现过的品种）。</summary>
    public required IReadOnlyList<string> Products { get; init; }
}
