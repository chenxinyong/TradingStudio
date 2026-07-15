namespace TradingStudio.Core.Models;

/// <summary>
/// 陈旧快照检测 — CTP 重连/重订阅时回推的快照 tick 带的是"最后一次行情刷新"的
/// 交易所时间戳（如午休 12:15 重连收到 UpdateTime=11:30:00 的快照）。
/// 这类 tick 喂给聚合器会生成零成交量垃圾 bar / 污染日线。
///
/// 只比较 time-of-day、不比较完整时间戳：ExchangeTimestamp 由 CTP TradingDay+UpdateTime
/// 拼成——夜盘 21:00-23:59 的日期是下一交易日（比 calendar 早收到一天，周五夜盘差三天），
/// 完整时间戳不可比；time-of-day 用圆环距离可比（处理跨午夜环绕）。
/// </summary>
public static class StaleTickDetector
{
    /// <summary>陈旧判定阈值（秒）。正常推送延迟为亚秒级，3 分钟给足了网络抖动余量。</summary>
    public const int DefaultThresholdSeconds = 180;

    private const int SecondsPerDay = 86400;

    /// <summary>两个 time-of-day 的圆环距离（秒）：23:59 与 00:01 距离是 120s 而非 23h58m</summary>
    public static int CircularDistanceSeconds(TimeSpan a, TimeSpan b)
    {
        var d = Math.Abs((int)a.TotalSeconds - (int)b.TotalSeconds) % SecondsPerDay;
        return Math.Min(d, SecondsPerDay - d);
    }

    /// <summary>交易所时间与本地接收时间（均为北京 time-of-day）偏差超阈值 → 判为陈旧快照</summary>
    public static bool IsStale(TimeSpan exchangeTod, TimeSpan localBeijingTod,
                               int thresholdSeconds = DefaultThresholdSeconds)
        => CircularDistanceSeconds(exchangeTod, localBeijingTod) > thresholdSeconds;
}
