using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Strategy;

/// <summary>
/// SMA233核心 + 多时间框架 SMA/MACD 策略。
///
/// 核心逻辑（15min主周期，做多+做空可配置）:
///   入场: 价格穿越 SMA233 + SMA排列 + MACD方向确认
///   出场1: 硬止损 — 单笔亏损 ≥ StopLossPct
///   出场2: ATR移动止损 — 价格创新高/低时止损位自动跟随（默认启用）
///   出场3: SMA移动止损 — 盈利达标后跟踪SMA线（可选）
///
/// 三个时间框架均计算 SMA(5/13/34/89/233) + MACD(12/26/9):
///   15min — 主决策周期（内部从1min聚合）
///   日线  — 趋势方向过滤（可选）
///   1min  — 动量确认（可选）
///
/// 用法: 引擎订阅1min Bar（BarPeriodMinutes=1），策略内部聚合到15min+日线
/// </summary>
public class SmaMacdStrategy : IStrategy
{
    // ── 策略参数 (v2: StrategyParam<T>, 借鉴 StockSharp) ──

    public StrategyParam<string> SmaPeriodsStr { get; } = new("SmaPeriodsStr", "5,13,34,89,233")
        { Group = "Signal", Description = "SMA周期列表(逗号分隔)" };

    public StrategyParam<int> MacdFast { get; } = new("MacdFast", 12)
        { Group = "Signal", Description = "MACD快线周期", OptimizeRange = (5, 50, 5) };

    public StrategyParam<int> MacdSlow { get; } = new("MacdSlow", 26)
        { Group = "Signal", Description = "MACD慢线周期", OptimizeRange = (10, 100, 10) };

    public StrategyParam<int> MacdSignal { get; } = new("MacdSignal", 9)
        { Group = "Signal", Description = "MACD信号线周期", OptimizeRange = (3, 30, 3) };

    public StrategyParam<double> MaxPositionRatio { get; } = new("MaxPositionRatio", 0.25)
        { Group = "Position", Description = "最大仓位占比", OptimizeRange = (0.05, 0.50, 0.05) };

    public StrategyParam<double> StopLossPct { get; } = new("StopLossPct", 0.02)
        { Group = "Risk", Description = "单笔止损比例", OptimizeRange = (0.005, 0.10, 0.01) };

    public StrategyParam<double> TakeProfitPct { get; } = new("TakeProfitPct", 0.04)
        { Group = "Risk", Description = "单笔止盈比例 (0=关闭)", OptimizeRange = (0, 0.20, 0.02) };

    public StrategyParam<int> MaxLots { get; } = new("MaxLots", 20)
        { Group = "Position", Description = "最大开仓手数", OptimizeRange = (1, 100, 10) };

    public StrategyParam<bool> RequireSmaAlignment { get; } = new("RequireSmaAlignment", true)
        { Group = "Entry", Description = "要求SMA多头排列(SMA5>13>34)" };

    public StrategyParam<bool> RequireMacdConfirm { get; } = new("RequireMacdConfirm", true)
        { Group = "Entry", Description = "要求MACD方向确认(DIF>DEA)" };

    public StrategyParam<bool> RequireDailyTrend { get; } = new("RequireDailyTrend", false)
        { Group = "Entry", Description = "要求日线趋势向上(日线SMA5>SMA34)" };

    public StrategyParam<bool> Require1minConfirm { get; } = new("Require1minConfirm", false)
        { Group = "Entry", Description = "要求1min MACD确认(DIF>DEA)" };

    public StrategyParam<bool> AllowShort { get; } = new("AllowShort", true)
        { Group = "Entry", Description = "允许做空(关闭则仅做多)" };

    // ── ATR 移动止损 ──
    public StrategyParam<bool> UseAtrTrail { get; } = new("UseAtrTrail", true)
        { Group = "Exit", Description = "ATR移动止损: 启用" };

    public StrategyParam<int> TrailAtrPeriod { get; } = new("TrailAtrPeriod", 20)
        { Group = "Exit", Description = "ATR移动止损: ATR周期", OptimizeRange = (10, 40, 5) };

    public StrategyParam<double> TrailAtrMult { get; } = new("TrailAtrMult", 2.0)
        { Group = "Exit", Description = "ATR移动止损: ATR倍数", OptimizeRange = (1.0, 5.0, 0.5) };

    // ── SMA 移动止损 ──
    public StrategyParam<bool> UseSmaTrail { get; } = new("UseSmaTrail", false)
        { Group = "Exit", Description = "SMA移动止损: 启用(盈利达标后跟踪SMA线)" };

    public StrategyParam<int> TrailSmaPeriod { get; } = new("TrailSmaPeriod", 89)
        { Group = "Exit", Description = "SMA移动止损: 跟踪SMA(13/34/89)", OptimizeRange = (13, 233, 20) };

    public StrategyParam<double> TrailActivationPct { get; } = new("TrailActivationPct", 0.03)
        { Group = "Exit", Description = "SMA移动止损: 盈利阈值(激活跟踪)", OptimizeRange = (0.01, 0.10, 0.01) };

    public StrategyParam<double> MinVolatilityPct { get; } = new("MinVolatilityPct", 0)
        { Group = "Filter", Description = "波动率过滤: 最低ATR/Price(0=关闭)", OptimizeRange = (0, 0.02, 0.002) };

    public StrategyParam<int> CooldownBars { get; } = new("CooldownBars", 0)
        { Group = "Filter", Description = "冷却期: 止损出场后禁止入场K线数", OptimizeRange = (0, 50, 5) };

    public StrategyParam<int> EntryPullbackSma { get; } = new("EntryPullbackSma", 0)
        { Group = "Entry", Description = "等回调入场: 回踩此SMA(0=关闭)", OptimizeRange = (0, 233, 20) };

    public string Name => "SMA233+MACD多时间框架";

    // ═══ 内部状态 ═══

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, InstrumentState> _state = new();
    private int[] _smaPeriods = [5, 13, 34, 89, 233];

    private const int SMA233_INDEX = 4; // _smaPeriods[4] = 233

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        _smaPeriods = ParseSmaPeriods(SmaPeriodsStr);

        foreach (var inst in context.SubscribedInstruments)
        {
            _state[inst] = new InstrumentState(_smaPeriods, MacdFast, MacdSlow, MacdSignal);
        }

        var trailMode = UseAtrTrail ? $"ATR({TrailAtrPeriod}/{TrailAtrMult}x)" :
                        UseSmaTrail ? $"SMA{TrailSmaPeriod}({TrailActivationPct:P0})" : "仅SMA233止盈";
        _ctx.Log($"初始化: {Name} on [{string.Join(", ", context.SubscribedInstruments)}] " +
                 $"SMA={string.Join("/", _smaPeriods)} MACD({MacdFast},{MacdSlow},{MacdSignal}) " +
                 $"MaxPos={MaxPositionRatio:P0} SL={StopLossPct:P1} " +
                 $"{(TakeProfitPct > 0 ? $"TP={TakeProfitPct:P1} (R={TakeProfitPct/StopLossPct:F1}:1)" : "TP=关闭")} " +
                 $"移动止损={trailMode}");
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;

        var price = bar.CloseDouble;

        // ── 1. 更新 1min 指标 ──
        s.Tf1m.UpdateSma(price);
        s.Tf1m.UpdateMacd(price);

        // ── 2. 聚合到 15min bucket ──
        var bucketTime = RoundDownToMinutes(bar.BarTime, 15);
        if (s._15mBucketTime != null && bucketTime != s._15mBucketTime.Value)
        {
            Flush15MinBar(bar.InstrumentId, s);
        }

        if (s._15mBucketTime == null || bucketTime != s._15mBucketTime.Value)
        {
            s._15mBucketTime = bucketTime;
            s._15mOpen = bar.OpenDouble;
            s._15mHigh = bar.HighDouble;
            s._15mLow = bar.LowDouble;
            s._15mClose = bar.CloseDouble;
            s._15mVolume = bar.Volume;
        }
        else
        {
            if (bar.HighDouble > s._15mHigh) s._15mHigh = bar.HighDouble;
            if (bar.LowDouble < s._15mLow) s._15mLow = bar.LowDouble;
            s._15mClose = bar.CloseDouble;
            s._15mVolume += bar.Volume;
        }
    }

    /// <summary>
    /// 15min Bar 完成时的处理：更新15min指标 → ATR → 聚合日线 → 评估信号
    /// </summary>
    private void Flush15MinBar(string instId, InstrumentState s)
    {
        var barPrice = s._15mClose;
        var barHigh = s._15mHigh;
        var barLow = s._15mLow;

        var prev15mSma233 = s._prev15mSma233;
        var prev15mClose = s._prev15mClose;
        s._prev15mSma5 = s.Tf15m.Sma(0);
        s._prev15mSma13 = s.Tf15m.Sma(1);
        s._prev15mSma34 = s.Tf15m.Sma(2);
        s._prev15mClose = barPrice;

        s.Tf15m.UpdateSma(barPrice);
        s.Tf15m.UpdateMacd(barPrice);
        s._prev15mSma233 = s.Tf15m.Sma(SMA233_INDEX);

        // ── 更新 ATR（波动率过滤/ATR移动止损共用）──
        bool needAtr = MinVolatilityPct > 0 || UseAtrTrail;
        if (needAtr)
        {
            UpdateAtr(s, barHigh, barLow, barPrice);
            if (UseAtrTrail) UpdateAtrTrailStop(s, barHigh, barLow);
        }

        // ── 聚合到日线 ──
        var barDay = DateOnly.FromDateTime(s._15mBucketTime!.Value);
        if (s._dayBucket != null && barDay != s._dayBucket.Value)
            FlushDayBar(s);

        if (s._dayBucket == null || barDay != s._dayBucket.Value)
        {
            s._dayBucket = barDay;
            s._dayOpen = barPrice;
            s._dayHigh = barHigh;
            s._dayLow = barLow;
            s._dayClose = barPrice;
        }
        else
        {
            if (barHigh > s._dayHigh) s._dayHigh = barHigh;
            if (barLow < s._dayLow) s._dayLow = barLow;
            s._dayClose = barPrice;
        }

        // ── 预热模式 ──
        if (_ctx.IsWarmup)
        {
            s.Warm15mCount++;
            if (s.Warm15mCount % 1000 == 0 && s.Tf15m.IsSmaReady(SMA233_INDEX))
            {
                _ctx.Log($"{instId}: 预热中… 15min bars={s.Warm15mCount}, days={s.WarmDayCount}, " +
                         $"SMA233={s.Tf15m.Sma(SMA233_INDEX):F2}" +
                         (UseAtrTrail && !double.IsNaN(s._atrValue) ? $" ATR={s._atrValue:F2}" : ""));
            }
            return;
        }

        if (!s._warmupDone)
        {
            s._warmupDone = true;
            _ctx.Log($"{instId}: 预热完成 → 开始交易. " +
                     $"15min bars={s.Warm15mCount}, days={s.WarmDayCount}, " +
                     $"SMA233={s.Tf15m.Sma(SMA233_INDEX):F2}" +
                     (UseAtrTrail && !double.IsNaN(s._atrValue) ? $" ATR={s._atrValue:F2}" : ""));
        }

        // ═══ 信号评估 ═══

        if (s._cooldownRemaining > 0) s._cooldownRemaining--;

        var cur15mSma233 = s.Tf15m.Sma(SMA233_INDEX);
        if (!s.Tf15m.IsSmaReady(SMA233_INDEX) || !s.Tf15m.IsMacdReady) return;

        var sma233Val = s.Tf15m.Sma(SMA233_INDEX);
        if (barPrice < sma233Val * 0.5 || barPrice > sma233Val * 2.0)
        {
            if (barPrice > 0)
                _ctx.LogWarning($"{instId}: 异常价格忽略@{s._15mBucketTime:HH:mm} C={barPrice:F2} SMA233={sma233Val:F2}");
            return;
        }

        // ── 波动率过滤：ATR/Price 低于阈值 → 横盘震荡，跳过交易 ──
        if (MinVolatilityPct > 0 && !double.IsNaN(s._atrValue) && barPrice > 0)
        {
            var volPct = s._atrValue / barPrice;
            if (volPct < MinVolatilityPct) return; // 波动率太低，不交易
        }

        var pos = _ctx.GetPosition(instId);
        var hasLong = pos is not null && pos.Quantity > 0;
        var hasShort = pos is not null && pos.Quantity < 0;

        if (!hasLong && !hasShort)
            CheckEntry(instId, s, barPrice, prev15mClose, prev15mSma233, cur15mSma233);
        else
            CheckExit(instId, s, barPrice, prev15mClose, prev15mSma233, cur15mSma233);
    }

    private void FlushDayBar(InstrumentState s)
    {
        s.TfDay.UpdateSma(s._dayClose);
        s.TfDay.UpdateMacd(s._dayClose);
        s.WarmDayCount++;
    }

    // ═══════════════════════════════════════════
    // 入场逻辑
    // ═══════════════════════════════════════════

    private void ExecuteLong(string instId, InstrumentState s, double curClose, double curSma233)
    {
        var lots = CalcLots(curClose, instId);
        if (lots > 0)
        {
            _ctx.MarketBuy(instId, lots, $"做多@{curClose:F2}");
            s._entryPrice = curClose;
            s._entryDir = 1;
            _ctx.Log($"入场: {instId} 做多 {lots}手 @ {curClose:F2} SMA233={curSma233:F2}");
        }
    }

    private void ExecuteShort(string instId, InstrumentState s, double curClose, double curSma233)
    {
        var lots = CalcLots(curClose, instId);
        if (lots > 0)
        {
            _ctx.MarketSell(instId, lots, $"做空@{curClose:F2}");
            s._entryPrice = curClose;
            s._entryDir = -1;
            _ctx.Log($"入场: {instId} 做空 {lots}手 @ {curClose:F2} SMA233={curSma233:F2}");
        }
    }

    private void CheckEntry(string instId, InstrumentState s, double curClose,
        double prevClose, double prevSma233, double curSma233)
    {
        if (double.IsNaN(prevSma233)) return;

        // 冷却期：止损出场后禁止立即重新入场
        if (CooldownBars > 0 && s._cooldownRemaining > 0) return;

        // ── 等回调入场：已在等待中，检查回调到位 ──
        if (EntryPullbackSma > 0 && s._waitingForPullback != 0)
        {
            var pbSma = double.NaN;
            for (int i = 0; i < _smaPeriods.Length; i++)
                if (_smaPeriods[i] == EntryPullbackSma) { pbSma = s.Tf15m.Sma(i); break; }
            if (double.IsNaN(pbSma)) return;
            if (s._waitingForPullback == 1 && curClose <= pbSma)
            {
                s._waitingForPullback = 0;
                // 多头回调到位 → 直接入场（仍需排列+MACD确认）
                if (RequireSmaAlignment)
                {
                    var a5 = s.Tf15m.Sma(0); var a13 = s.Tf15m.Sma(1); var a34 = s.Tf15m.Sma(2);
                    if (!(a5 > a13 && a13 > a34)) return;
                }
                if (RequireMacdConfirm && !(s.Tf15m.MacdDif > s.Tf15m.MacdDea)) return;
                ExecuteLong(instId, s, curClose, curSma233);
                return;
            }
            else if (s._waitingForPullback == -1 && curClose >= pbSma)
            {
                s._waitingForPullback = 0;
                if (RequireSmaAlignment)
                {
                    var a5 = s.Tf15m.Sma(0); var a13 = s.Tf15m.Sma(1); var a34 = s.Tf15m.Sma(2);
                    if (!(a5 < a13 && a13 < a34)) return;
                }
                if (RequireMacdConfirm && !(s.Tf15m.MacdDif < s.Tf15m.MacdDea)) return;
                ExecuteShort(instId, s, curClose, curSma233);
                return;
            }
            return; // 还没回调到位，继续等
        }

        bool longSignal = prevClose <= prevSma233 && curClose > curSma233;
        if (longSignal)
        {
            if (RequireSmaAlignment)
            {
                var s5 = s.Tf15m.Sma(0); var s13 = s.Tf15m.Sma(1); var s34 = s.Tf15m.Sma(2);
                if (!(s5 > s13 && s13 > s34)) longSignal = false;
            }
            if (RequireMacdConfirm && longSignal)
                if (!(s.Tf15m.MacdDif > s.Tf15m.MacdDea)) longSignal = false;
            if (RequireDailyTrend && longSignal)
            {
                if (!s.TfDay.IsSmaReady(0) || !s.TfDay.IsSmaReady(2)) longSignal = false;
                else if (!(s.TfDay.Sma(0) > s.TfDay.Sma(2))) longSignal = false;
            }
            if (Require1minConfirm && longSignal)
            {
                if (!s.Tf1m.IsMacdReady) longSignal = false;
                else if (!(s.Tf1m.MacdDif > s.Tf1m.MacdDea)) longSignal = false;
            }
        }

        bool shortSignal = AllowShort &&
                           prevClose >= prevSma233 && curClose < curSma233;
        if (shortSignal)
        {
            if (RequireSmaAlignment)
            {
                var s5 = s.Tf15m.Sma(0); var s13 = s.Tf15m.Sma(1); var s34 = s.Tf15m.Sma(2);
                if (!(s5 < s13 && s13 < s34)) shortSignal = false;
            }
            if (RequireMacdConfirm && shortSignal)
                if (!(s.Tf15m.MacdDif < s.Tf15m.MacdDea)) shortSignal = false;
            if (RequireDailyTrend && shortSignal)
            {
                if (!s.TfDay.IsSmaReady(0) || !s.TfDay.IsSmaReady(2)) shortSignal = false;
                else if (!(s.TfDay.Sma(0) < s.TfDay.Sma(2))) shortSignal = false;
            }
            if (Require1minConfirm && shortSignal)
            {
                if (!s.Tf1m.IsMacdReady) shortSignal = false;
                else if (!(s.Tf1m.MacdDif < s.Tf1m.MacdDea)) shortSignal = false;
            }
        }

        if (longSignal)
        {
            var lots = CalcLots(curClose, instId);
            if (lots > 0)
            {
                _ctx.MarketBuy(instId, lots, $"SMA233上穿@{curClose:F2}");
                s._entryPrice = curClose;
                s._entryDir = 1;
                if (UseAtrTrail && !double.IsNaN(s._atrValue))
                {
                    s._trailStop = curClose - TrailAtrMult * s._atrValue;
                    _ctx.Log($"入场: {instId} 做多 {lots}手 @ {curClose:F2} SMA233={curSma233:F2} ATR止={s._trailStop:F2}");
                }
                else
                {
                    _ctx.Log($"入场: {instId} 做多 {lots}手 @ {curClose:F2} SMA233={curSma233:F2}");
                }
            }
        }
        else if (shortSignal)
        {
            var lots = CalcLots(curClose, instId);
            if (lots > 0)
            {
                _ctx.MarketSell(instId, lots, $"SMA233下穿@{curClose:F2}");
                s._entryPrice = curClose;
                s._entryDir = -1;
                if (UseAtrTrail && !double.IsNaN(s._atrValue))
                {
                    s._trailStop = curClose + TrailAtrMult * s._atrValue;
                    _ctx.Log($"入场: {instId} 做空 {lots}手 @ {curClose:F2} SMA233={curSma233:F2} ATR止={s._trailStop:F2}");
                }
                else
                {
                    _ctx.Log($"入场: {instId} 做空 {lots}手 @ {curClose:F2} SMA233={curSma233:F2}");
                }
            }
        }
    }

    // ═══════════════════════════════════════════
    // 出场逻辑
    // ═══════════════════════════════════════════

    private void CheckExit(string instId, InstrumentState s, double curClose,
        double prevClose, double prevSma233, double curSma233)
    {
        var pos = _ctx.GetPosition(instId);
        if (pos == null) return;

        bool isLong = pos.Quantity > 0;
        bool isShort = pos.Quantity < 0;
        bool exit = false;
        string reason = "";

        // ① 硬止损：单笔亏损 >= StopLossPct
        if (s._entryPrice.HasValue)
        {
            double pnlPct;
            if (isLong)
                pnlPct = (curClose - s._entryPrice.Value) / s._entryPrice.Value;
            else
                pnlPct = (s._entryPrice.Value - curClose) / s._entryPrice.Value;

            // 止盈 (R倍数止盈, 优先于止损检查)
            if (TakeProfitPct > 0 && pnlPct >= TakeProfitPct)
            {
                exit = true;
                reason = $"止盈@{curClose:F2} (盈利={pnlPct:P1} R={(pnlPct/StopLossPct):F1}:1)";
            }
            else if (pnlPct <= -StopLossPct)
            {
                exit = true;
                reason = $"硬止损@{curClose:F2} (亏损={pnlPct:P1})";
            }
        }

        // ② ATR移动止损（优先：价格创新高/低自动跟随，无需激活阈值）
        if (!exit && UseAtrTrail && !double.IsNaN(s._trailStop))
        {
            if (isLong && curClose <= s._trailStop)
            {
                exit = true;
                reason = $"ATR移动止损@{curClose:F2} (止={s._trailStop:F2} ATR={s._atrValue:F2})";
            }
            else if (isShort && curClose >= s._trailStop)
            {
                exit = true;
                reason = $"ATR移动止损@{curClose:F2} (止={s._trailStop:F2} ATR={s._atrValue:F2})";
            }
        }

        // ③ SMA移动止损（盈利达标后跟踪SMA线）
        if (!exit && UseSmaTrail)
        {
            double pnlPct;
            if (isLong)
                pnlPct = s._entryPrice.HasValue ? (curClose - s._entryPrice.Value) / s._entryPrice.Value : 0;
            else
                pnlPct = s._entryPrice.HasValue ? (s._entryPrice.Value - curClose) / s._entryPrice.Value : 0;

            if (pnlPct >= TrailActivationPct)
            {
                var trailSma = GetTrailSma(s);
                if (!double.IsNaN(trailSma))
                {
                    if (isLong && curClose < trailSma)
                    {
                        exit = true;
                        reason = $"SMA{TrailSmaPeriod}移动止损@{curClose:F2} (线={trailSma:F2} 盈={pnlPct:P1})";
                    }
                    else if (isShort && curClose > trailSma)
                    {
                        exit = true;
                        reason = $"SMA{TrailSmaPeriod}移动止损@{curClose:F2} (线={trailSma:F2} 盈={pnlPct:P1})";
                    }
                }
            }
        }

        // ④ SMA233止盈（兜底：无ATR也无SMA移动止损时）
        if (!exit && !UseAtrTrail && !UseSmaTrail && !double.IsNaN(prevSma233))
        {
            if (isLong)
            {
                bool crossBelow = prevClose >= prevSma233 && curClose < curSma233;
                if (crossBelow && pos.UnrealizedPnl > 0)
                {
                    exit = true;
                    reason = $"SMA233下穿止盈@{curClose:F2}";
                }
            }
            else if (isShort)
            {
                bool crossAbove = prevClose <= prevSma233 && curClose > curSma233;
                if (crossAbove && pos.UnrealizedPnl > 0)
                {
                    exit = true;
                    reason = $"SMA233上穿止盈@{curClose:F2}";
                }
            }
        }

        if (exit)
        {
            var slReason = reason.Contains("硬止损") ? "SL"
                : reason.Contains("止损") ? "SL"
                : reason.Contains("止盈") ? "TP"
                : reason.Contains("上穿") || reason.Contains("下穿") ? "Signal"
                : "Signal";
            _ctx.ClosePosition(instId, slReason);
            s._entryPrice = null;
            s._entryDir = 0;
            s._trailStop = double.NaN;
            if (CooldownBars > 0 && reason.Contains("硬止损"))
                s._cooldownRemaining = CooldownBars;
            _ctx.Log($"出场: {instId} {reason}" +
                     (s._cooldownRemaining > 0 ? $" (冷却{CooldownBars}K)" : ""));
        }
    }

    // ═══════════════════════════════════════════
    // ATR + ATR 移动止损
    // ═══════════════════════════════════════════

    /// <summary>
    /// 更新 15min ATR（SMA of True Range）。
    /// True Range = max(H-L, |H-PrevC|, |L-PrevC|)
    /// </summary>
    private void UpdateAtr(InstrumentState s, double barHigh, double barLow, double barClose)
    {
        if (!double.IsNaN(s._prevCloseForAtr))
        {
            var tr = Math.Max(barHigh - barLow,
                Math.Max(Math.Abs(barHigh - s._prevCloseForAtr),
                         Math.Abs(barLow - s._prevCloseForAtr)));
            s._atrTrQueue.Enqueue(tr);
            s._atrTrSum += tr;
            if (s._atrTrQueue.Count > TrailAtrPeriod)
                s._atrTrSum -= s._atrTrQueue.Dequeue();
            if (s._atrTrQueue.Count >= TrailAtrPeriod)
                s._atrValue = s._atrTrSum / TrailAtrPeriod;
        }
        s._prevCloseForAtr = barClose;
    }

    /// <summary>
    /// 更新 ATR 移动止损位：价格创新高（多头）或新低（空头）时，止损位跟随上移/下移。
    /// 止损位只向有利方向移动，永不倒退。
    /// </summary>
    private void UpdateAtrTrailStop(InstrumentState s, double barHigh, double barLow)
    {
        if (double.IsNaN(s._trailStop) || double.IsNaN(s._atrValue)) return;
        var offset = TrailAtrMult * s._atrValue;

        if (s._entryDir == 1) // 多头：止损跟随上移
        {
            var newStop = barHigh - offset;
            if (newStop > s._trailStop) s._trailStop = newStop;
        }
        else if (s._entryDir == -1) // 空头：止损跟随下移
        {
            var newStop = barLow + offset;
            if (newStop < s._trailStop) s._trailStop = newStop;
        }
    }

    // ═══════════════════════════════════════════
    // SMA 移动止损辅助
    // ═══════════════════════════════════════════

    private double GetTrailSma(InstrumentState s)
    {
        for (int i = 0; i < _smaPeriods.Length; i++)
            if (_smaPeriods[i] == TrailSmaPeriod)
                return s.Tf15m.Sma(i);
        return double.NaN;
    }

    // ═══════════════════════════════════════════
    // 仓位计算
    // ═══════════════════════════════════════════

    private int CalcLots(double price, string instId)
    {
        var equity = (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital);
        var f = _ctx.GetFuture(instId);
        var mult = (double)(f?.TradingUnit ?? 10m);
        var marginRate = (double)(f?.MarginRate ?? 0.08m);
        var contractValue = price * mult;

        var maxNotional = equity * MaxPositionRatio;
        int lots = (int)(maxNotional / contractValue);
        if (lots < 1) lots = 1;

        var marginPerLot = contractValue * marginRate;
        while (lots > 0 && marginPerLot * lots > equity * MaxPositionRatio)
            lots--;

        if (lots > MaxLots) lots = MaxLots;
        return lots;
    }

    // ═══════════════════════════════════════════
    // 工具方法
    // ═══════════════════════════════════════════

    private static DateTime RoundDownToMinutes(DateTime time, int minutes)
    {
        var ticks = time.Ticks;
        var periodTicks = TimeSpan.FromMinutes(minutes).Ticks;
        return new DateTime(ticks / periodTicks * periodTicks, time.Kind);
    }

    private static int[] ParseSmaPeriods(string str)
    {
        return str.Split(',', StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => int.Parse(s.Trim()))
                  .ToArray();
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (evt.Type == OrderEventType.Filled)
            _ctx.Log($"成交: {evt.InstrumentId} {evt.Direction} {evt.Quantity}手 @ {evt.FillPrice:F2}");
    }

    public void OnEndOfAlgorithm()
    {
        foreach (var (inst, s) in _state)
        {
            _ctx.Log($"{inst}: 15min bars={s.Warm15mCount}, days={s.WarmDayCount}, " +
                     $"final SMA233={s.Tf15m.Sma(SMA233_INDEX):F2}");
        }
        _ctx.Log($"回测结束. 最终权益: {_ctx.Equity:C}");
    }

    // ═══════════════════════════════════════════
    // 内部类型
    // ═══════════════════════════════════════════

    private class SmaState
    {
        private readonly int _period;
        private readonly Queue<double> _window = new();
        private double _sum;
        public double Value { get; private set; } = double.NaN;
        public bool IsReady => _window.Count >= _period;

        public SmaState(int period) => _period = period;

        public void Update(double price)
        {
            _window.Enqueue(price);
            _sum += price;
            if (_window.Count > _period) _sum -= _window.Dequeue();
            if (IsReady) Value = _sum / _period;
        }
    }

    private class MacdState
    {
        private readonly double _fastAlpha, _slowAlpha, _signalAlpha;
        private double _fastEma = double.NaN, _slowEma = double.NaN, _signalEma = double.NaN;
        public double Dif { get; private set; } = double.NaN;
        public double Dea { get; private set; } = double.NaN;
        public double Hist { get; private set; } = double.NaN;
        public bool IsReady => !double.IsNaN(Dea);

        public MacdState(int fast, int slow, int signal)
        {
            _fastAlpha = 2.0 / (fast + 1);
            _slowAlpha = 2.0 / (slow + 1);
            _signalAlpha = 2.0 / (signal + 1);
        }

        public void Update(double price)
        {
            _fastEma = double.IsNaN(_fastEma) ? price : _fastEma + _fastAlpha * (price - _fastEma);
            _slowEma = double.IsNaN(_slowEma) ? price : _slowEma + _slowAlpha * (price - _slowEma);
            Dif = _fastEma - _slowEma;
            _signalEma = double.IsNaN(_signalEma) ? Dif : _signalEma + _signalAlpha * (Dif - _signalEma);
            Dea = _signalEma;
            Hist = 2 * (Dif - Dea);
        }
    }

    private class TfState
    {
        private readonly SmaState[] _smas;
        private readonly MacdState _macd;

        public double MacdDif => _macd.Dif;
        public double MacdDea => _macd.Dea;
        public double MacdHist => _macd.Hist;
        public bool IsMacdReady => _macd.IsReady;

        public TfState(int[] smaPeriods, int macdFast, int macdSlow, int macdSignal)
        {
            _smas = smaPeriods.Select(p => new SmaState(p)).ToArray();
            _macd = new MacdState(macdFast, macdSlow, macdSignal);
        }

        public double Sma(int index) => _smas[index].Value;
        public bool IsSmaReady(int index) => _smas[index].IsReady;

        public void UpdateSma(double price)
        {
            foreach (var sma in _smas) sma.Update(price);
        }

        public void UpdateMacd(double price) => _macd.Update(price);
    }

    private class InstrumentState
    {
        public readonly TfState Tf1m, Tf15m, TfDay;

        // 15min 聚合中
        public DateTime? _15mBucketTime;
        public double _15mOpen, _15mHigh, _15mLow, _15mClose;
        public long _15mVolume;

        // 上一根 15min Bar 的快照（用于穿越检测）
        public double _prev15mClose = double.NaN;
        public double _prev15mSma233 = double.NaN;
        public double _prev15mSma5 = double.NaN;
        public double _prev15mSma13 = double.NaN;
        public double _prev15mSma34 = double.NaN;

        // 日线聚合中
        public DateOnly? _dayBucket;
        public double _dayOpen, _dayHigh, _dayLow, _dayClose;

        // 入场价 + 方向（用于止损计算）
        public double? _entryPrice;
        public int _entryDir; // 1=long, -1=short, 0=none

        // 冷却期（止损后禁止入场）
        public int _cooldownRemaining; // 剩余冷却K线数

        // 等回调入场
        public int _waitingForPullback; // 0=无, 1=等多头回调, -1=等空头回调

        // ATR + ATR 移动止损
        public double _atrValue = double.NaN;
        public double _prevCloseForAtr = double.NaN;
        public double _trailStop = double.NaN;
        public readonly Queue<double> _atrTrQueue = new();
        public double _atrTrSum;

        // 统计
        public int Warm15mCount;
        public int WarmDayCount;
        public bool _warmupDone;

        public InstrumentState(int[] smaPeriods, int macdFast, int macdSlow, int macdSignal)
        {
            Tf1m = new TfState(smaPeriods, macdFast, macdSlow, macdSignal);
            Tf15m = new TfState(smaPeriods, macdFast, macdSlow, macdSignal);
            TfDay = new TfState(smaPeriods, macdFast, macdSlow, macdSignal);
        }
    }
}
