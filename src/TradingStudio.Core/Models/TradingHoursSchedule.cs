namespace TradingStudio.Core.Models;

/// <summary>
/// 品种交易时段表 — 解析 symbols.json 的 tradingHours 字符串，
/// 判定某个 time-of-day（北京墙钟）是否落在交易时段内。
///
/// 已知格式变体（2026-07 实测 symbols.json 共 6 种）：
///   "09:00-10:15,10:30-11:30,13:30-15:00"                      （无夜盘）
///   "09:00-10:15,10:30-11:30,13:30-15:00; 夜盘:21:00-23:00"    （夜盘不跨午夜）
///   "09:00-10:15,10:30-11:30,13:30-15:00; 夜盘:21:00-01:00"    （夜盘跨午夜）
///   "09:00-10:15,10:30-11:30,13:30-15:00; 夜盘:21:00-02:30"    （夜盘跨午夜）
///   "09:30-11:30,13:00-15:00"                                  （中金所股指）
///   "09:30-11:30,13:00-15:15"                                  （中金所国债）
/// </summary>
public sealed class TradingHoursSchedule
{
    /// <summary>开盘前宽限（秒）：集合竞价成交在 08:59/20:59 推送，是真实数据必须保留</summary>
    public const int OpenGraceSeconds = 60;

    /// <summary>收盘后宽限（秒）：收盘 tick / 结算推送可能带 11:30:xx / 15:00:xx 时间戳</summary>
    public const int CloseGraceSeconds = 60;

    private const int SecondsPerDay = 86400;

    // 已展开宽限、已拆跨午夜的闭区间（秒）
    private readonly List<(int StartSec, int EndSec)> _ranges;

    private TradingHoursSchedule(List<(int, int)> ranges) => _ranges = ranges;

    /// <summary>
    /// 解析 tradingHours 字符串。空串 / 全部段解析失败 → null（调用方按 fail-open 处理，放行）。
    /// </summary>
    public static TradingHoursSchedule? TryParse(string? tradingHours)
    {
        if (string.IsNullOrWhiteSpace(tradingHours)) return null;

        var ranges = new List<(int, int)>();
        foreach (var part in tradingHours.Split(';'))
        {
            var p = part.Trim();
            // 夜盘段前缀（防御性兼容全角冒号）
            if (p.StartsWith("夜盘:")) p = p["夜盘:".Length..];
            else if (p.StartsWith("夜盘：")) p = p["夜盘：".Length..];

            foreach (var seg in p.Split(','))
            {
                var range = ParseSegment(seg.Trim());
                if (range == null) continue;   // 单段乱格式跳过，不拖垮整串

                var (startSec, endSec) = range.Value;
                var s = startSec - OpenGraceSeconds;
                var e = endSec + CloseGraceSeconds;

                if (endSec <= startSec)
                {
                    // 跨午夜（如 21:00-01:00）：拆两段
                    ranges.Add((s, SecondsPerDay - 1));
                    ranges.Add((0, e));
                }
                else if (s < 0)
                {
                    // 宽限跨过 00:00（理论边界，防御处理）
                    ranges.Add((s + SecondsPerDay, SecondsPerDay - 1));
                    ranges.Add((0, e));
                }
                else
                {
                    ranges.Add((s, e));
                }
            }
        }

        return ranges.Count > 0 ? new TradingHoursSchedule(ranges) : null;
    }

    /// <summary>time-of-day（北京墙钟）是否在任一交易时段内（含宽限，闭区间）</summary>
    public bool Contains(TimeSpan timeOfDay)
    {
        var t = (int)timeOfDay.TotalSeconds % SecondsPerDay;
        foreach (var (start, end) in _ranges)
            if (t >= start && t <= end) return true;
        return false;
    }

    /// <summary>解析 "HH:mm-HH:mm" 单段 → (起秒, 止秒)；失败 → null</summary>
    private static (int Start, int End)? ParseSegment(string seg)
    {
        var dash = seg.IndexOf('-');
        if (dash <= 0 || dash >= seg.Length - 1) return null;

        if (!TryParseHhmm(seg[..dash], out var start)) return null;
        if (!TryParseHhmm(seg[(dash + 1)..], out var end)) return null;
        return (start, end);
    }

    private static bool TryParseHhmm(string s, out int seconds)
    {
        seconds = 0;
        var parts = s.Trim().Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
        if (h is < 0 or > 23 || m is < 0 or > 59) return false;
        seconds = h * 3600 + m * 60;
        return true;
    }
}
