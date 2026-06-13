using System.Globalization;

namespace TradingStudio.Data.Import;

/// <summary>
/// 金数源 RAR 条目路径解析 + Layer/Symbol/Exchange 过滤。
/// 所有过滤逻辑集中在此，不依赖 I/O。
/// </summary>
public static class JinshuyuanEntryFilter
{
    /// <summary>RAR 目录名 → CTP 交易所代码</summary>
    private static readonly Dictionary<string, string> ExchangeDirToCtp = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sc"] = "SHFE", ["dc"] = "DCE", ["zc"] = "CZCE",
        ["ine"] = "INE", ["gfex"] = "GFEX",
    };

    /// <summary>解析后的条目元数据</summary>
    public readonly record struct EntryMeta(
        string ExchangeDir,       // RAR 内目录: "sc", "dc", ...
        string ExchangeCode,      // CTP 代码: "SHFE", "DCE", ...
        string ContractCode,      // 完整合约代码: "cu2501", "ag主力连续"
        string ProductCode,       // 品种代码: "cu", "ag" (无法确定则为空)
        DateOnly TradingDay,      // 交易日
        bool IsMainContinuous,    // 含"主力连续"
        bool IsSubMainContinuous  // 含"次主力连续"
    );

    /// <summary>
    /// 解析 RAR 条目路径。
    /// 格式: {exchange}/{contract}_{YYYYMMDD}.csv
    /// 例: "sc/cu2501_20250601.csv", "dc/ag主力连续_20210104.csv"
    /// </summary>
    public static EntryMeta ParseEntryPath(string entryPath)
    {
        var parts = entryPath.Split('/');
        if (parts.Length != 2)
            throw new FormatException($"Invalid entry path format: {entryPath}");

        var exchangeDir = parts[0];
        var fileName = parts[1];

        if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Not a CSV: {entryPath}");

        var stem = fileName[..^4]; // strip ".csv"

        // 找最后一个 '_' 分割 合约代码 和 日期
        var lastUnderscore = stem.LastIndexOf('_');
        if (lastUnderscore < 0)
            throw new FormatException($"No underscore in filename: {entryPath}");

        var contractCode = stem[..lastUnderscore];
        var dateStr = stem[(lastUnderscore + 1)..];

        if (!DateOnly.TryParseExact(dateStr, "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var tradingDay))
            throw new FormatException($"Invalid date in filename: {entryPath}");

        // 交易所映射
        var exchangeCode = ExchangeDirToCtp.TryGetValue(exchangeDir, out var ctp)
            ? ctp : exchangeDir.ToUpperInvariant();

        // 连续合约检测
        var isMain = contractCode.Contains("主力连续");
        var isSub = contractCode.Contains("次主力连续");

        // 提取品种代码
        var productCode = ExtractProductCode(contractCode, isMain || isSub);

        return new EntryMeta(exchangeDir, exchangeCode, contractCode,
            productCode, tradingDay, isMain, isSub);
    }

    /// <summary>
    /// 判断条目是否匹配导入条件。
    /// </summary>
    public static bool Matches(string entryPath, JinshuyuanOptions options)
    {
        // 快速跳过非 CSV
        if (!entryPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return false;

        // UnRAR lb 用反斜杠，ParseEntryPath 用正斜杠
        var normalized = entryPath.Replace('\\', '/');

        if (!TryParseEntryPath(normalized, out var meta))
            return false;

        // Exchange 过滤
        if (options.ExchangeCode is not null &&
            !string.Equals(meta.ExchangeCode, options.ExchangeCode, StringComparison.OrdinalIgnoreCase))
            return false;

        // Layer 过滤
        switch (options.Layer)
        {
            case "main":
                if (!meta.IsMainContinuous) return false;
                break;
            case "active":
                // active = 非连续合约 + 品种在 symbols.json 中
                if (meta.IsMainContinuous || meta.IsSubMainContinuous) return false;
                if (string.IsNullOrEmpty(meta.ProductCode)) return false;
                if (!options.KnownProducts.Contains(meta.ProductCode)) return false;
                break;
            // "all": 不过滤
        }

        // Symbol 过滤 (叠加在 Layer 之上)
        if (options.Symbols.Count > 0)
        {
            if (string.IsNullOrEmpty(meta.ProductCode)) return false;
            if (!options.Symbols.Contains(meta.ProductCode)) return false;
        }

        return true;
    }

    /// <summary>安全版解析，失败返回 false 不抛异常</summary>
    private static bool TryParseEntryPath(string entryPath, out EntryMeta meta)
    {
        try { meta = ParseEntryPath(entryPath); return true; }
        catch { meta = default; return false; }
    }

    /// <summary>从合约代码提取品种代码</summary>
    private static string ExtractProductCode(string contractCode, bool isContinuous)
    {
        if (string.IsNullOrEmpty(contractCode)) return "";

        if (isContinuous)
        {
            // "ag主力连续" 或 "cu_主力连续" → 取前缀
            var idx = contractCode.IndexOf('_');
            if (idx > 0) return contractCode[..idx];
            // "ag主力连续" → TrimEnd 掉中文
            var code = contractCode;
            while (code.Length > 0 && code[^1] >= 0x4E00) // 中文字符范围
                code = code[..^1];
            return code.ToLowerInvariant();
        }

        // 普通合约: "cu2501" → "cu", "TA608" → "TA"
        return contractCode.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').ToLowerInvariant();
    }
}
