namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 背驰检测器。
/// 比较相邻同向笔的力度，判断是否出现趋势衰竭信号。
///
/// 判断维度:
///   1. 价格背驰: 第二笔未创新高/新低
///   2. 力度背驰 (Power): 缠论笔力度(万分比) 第二笔 < 第一笔
///   3. 速度背驰 (Speed): 单位时间涨跌幅 第二笔 < 第一笔
///   4. 长度背驰 (Range): 绝对涨跌点数 第二笔 < 第一笔
/// </summary>
public static class DivergenceDetector
{
    /// <summary>
    /// 对同向笔序列进行背驰检测。
    /// 比较每一对相邻同向笔 (Bi[i], Bi[i+1])。
    /// </summary>
    /// <param name="bis">笔列表（按时间升序）</param>
    /// <returns>背驰信号列表（只包含方向一致且出现衰竭信号的笔对）</returns>
    public static List<DivergenceSignal> Detect(List<Bi> bis)
    {
        if (bis.Count < 2)
            return [];

        var signals = new List<DivergenceSignal>();

        for (int i = 0; i < bis.Count - 1; i++)
        {
            var prev = bis[i];
            var curr = bis[i + 1];

            // 只比较同向笔
            if (prev.Type != curr.Type)
                continue;

            var signal = CompareBi(prev, curr);
            if (signal != null)
                signals.Add(signal);
        }

        return signals;
    }

    /// <summary>
    /// 比较两笔，返回背驰信号（无背驰则 null）。
    /// </summary>
    private static DivergenceSignal? CompareBi(Bi prev, Bi curr)
    {
        // ── 价格背驰 ──
        bool priceDivergence = curr.Type == Direction.Up
            ? curr.High <= prev.High   // 上涨笔：新高不及前一笔高
            : curr.Low >= prev.Low;    // 下跌笔：新低不及前一笔低

        // ── 力度背驰 (万分比) ──
        bool powerDivergence = curr.Power < prev.Power;

        // ── 速度背驰 (每周/日涨跌幅) ──
        int prevDays = (int)(prev.DtEnd - prev.DtStart).TotalDays;
        int currDays = (int)(curr.DtEnd - curr.DtStart).TotalDays;
        double prevSpeed = prevDays > 0 ? prev.ChangePct / prevDays : 0;
        double currSpeed = currDays > 0 ? curr.ChangePct / currDays : 0;
        bool speedDivergence = currSpeed < prevSpeed;

        // ── 长度背驰 (绝对涨跌点数) ──
        bool rangeDivergence = curr.ChangePct < prev.ChangePct;

        // ── 综合判断 ──
        int score = 0;
        if (priceDivergence) score++;
        if (powerDivergence) score++;
        if (speedDivergence) score++;
        if (rangeDivergence) score++;

        // 至少满足 2/4 条件才视为有效背驰信号
        if (score < 2)
            return null;

        var level = score switch
        {
            4 => DivergenceLevel.Strong,
            3 => DivergenceLevel.Clear,
            _ => DivergenceLevel.Weak,
        };

        return new DivergenceSignal(
            BiType: prev.Type,
            PrevBi: prev,
            CurrBi: curr,
            Level: level,
            Score: score,
            PriceDivergence: priceDivergence,
            PowerDivergence: powerDivergence,
            SpeedDivergence: speedDivergence,
            RangeDivergence: rangeDivergence,
            PrevSpeed: Math.Round(prevSpeed, 4),
            CurrSpeed: Math.Round(currSpeed, 4)
        );
    }
}

/// <summary>
/// 背驰信号记录。
/// </summary>
public record DivergenceSignal(
    Direction BiType,
    Bi PrevBi,
    Bi CurrBi,
    DivergenceLevel Level,
    int Score,                  // 2-4，满足的背驰维度数
    bool PriceDivergence,
    bool PowerDivergence,
    bool SpeedDivergence,
    bool RangeDivergence,
    double PrevSpeed,
    double CurrSpeed
)
{
    public string LevelLabel => Level switch
    {
        DivergenceLevel.Strong => "★★★ 强背驰",
        DivergenceLevel.Clear => "★★☆ 明确背驰",
        DivergenceLevel.Weak => "★☆☆ 弱背驰",
        _ => "",
    };

    public string DirectionLabel => BiType == Direction.Up ? "顶背驰(上涨衰竭)" : "底背驰(下跌衰竭)";
}

public enum DivergenceLevel
{
    Weak = 2,    // 满足 2/4
    Clear = 3,   // 满足 3/4
    Strong = 4,  // 满足 4/4
}
