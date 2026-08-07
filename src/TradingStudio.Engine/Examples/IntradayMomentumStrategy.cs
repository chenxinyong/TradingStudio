using System.Globalization;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;
using TradingStudio.Core.Sizing;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine.Examples;

/// <summary>
/// 日内动量因子策略 — IntradayMom = (Close_30min - Open) / Open。
///
/// 核心发现（Python IC分析）：开盘后前30分钟的涨跌方向，强烈预示次日收益方向。
/// IS IC_IR=+0.96, OOS IC_IR=+1.05, 多空Sharpe=22.86。
///
/// 交易逻辑：
///   日内动量 > 0 → 次日做多；日内动量 < 0 → 次日做空。
///   使用滚动252日Z-score过滤噪声，结合ATR止损/止盈。
///
/// 数据源：1min bars → 预计算 IntradayMom → CSV → 策略加载。
///   策略运行在 bars_day 上（每天一根K线），快速回测。
/// </summary>
public class IntradayMomentumStrategy : IStrategy
{
    public StrategyParam<double> ZEntryThreshold { get; } = new("ZEntryThreshold", 0.0)
        { Group = "Entry", Description = "Z-score 入场阈值 (|Z|>此值才开仓)", OptimizeRange = (0, 3.0, 0.5) };

    public StrategyParam<int> ZWindowDays { get; } = new("ZWindowDays", 252)
        { Group = "Entry", Description = "Z-score 滚动窗口天数", OptimizeRange = (60, 504, 20) };

    public StrategyParam<int> AtrPeriod { get; } = new("AtrPeriod", 20)
        { Group = "Risk", Description = "ATR周期", OptimizeRange = (10, 40, 10) };

    public StrategyParam<double> StopAtrMult { get; } = new("StopAtrMult", 2.0)
        { Group = "Risk", Description = "止损ATR倍数", OptimizeRange = (1.0, 5.0, 0.5) };

    public StrategyParam<double> TakeProfitAtrMult { get; } = new("TakeProfitAtrMult", 3.0)
        { Group = "Risk", Description = "止盈ATR倍数 (0=关闭)", OptimizeRange = (0, 10.0, 1.0) };

    public StrategyParam<double> RiskPerTrade { get; } = new("RiskPerTrade", 0.02)
        { Group = "Position", Description = "单笔风险占比", OptimizeRange = (0.005, 0.05, 0.005) };

    public StrategyParam<double> MaxMarginRatio { get; } = new("MaxMarginRatio", 0.25)
        { Group = "Position", Description = "最大保证金占比", OptimizeRange = (0.10, 0.50, 0.05) };

    public StrategyParam<int> MaxPosition { get; } = new("MaxPosition", 2)
        { Group = "Position", Description = "最大持仓手数", OptimizeRange = (1, 20, 1) };

    public StrategyParam<string> FactorCsvPath { get; } = new("FactorCsvPath", "")
        { Group = "Data", Description = "因子CSV路径 (相对于工作目录或绝对路径)" };

    public string Name => "日内动量因子(IntradayMom)";

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, InstrumentState> _state = new();

    // 预计算的因子值: (instrumentId, date) → IntradayMom原始值
    private readonly Dictionary<string, SortedDictionary<DateOnly, double>> _factorCache = new();

    public void Initialize(StrategyContext context)
    {
        _ctx = context;

        // 1. 加载因子 CSV
        var csvPath = ResolveCsvPath();
        if (!File.Exists(csvPath))
        {
            context.LogError($"因子CSV不存在: {csvPath}");
            return;
        }
        LoadFactorCsv(csvPath);
        context.Log($"加载因子: {_factorCache.Sum(kv => kv.Value.Count):N0} 条, {_factorCache.Count} 品种");

        // 2. 预热 ATR 指标
        foreach (var inst in context.SubscribedInstruments)
        {
            var history = context.GetBarHistory(inst);
            var s = new InstrumentState(AtrPeriod, ZWindowDays);
            foreach (var bar in history)
            {
                s.UpdateAtr(bar);
                // Feed IntradayMom 值到滚动窗口
                if (_factorCache.TryGetValue(inst, out var facDict) &&
                    facDict.TryGetValue(bar.TradingDay, out var im))
                {
                    s.PushIntradayMom(im, bar.TradingDay);
                }
            }
            _state[inst] = s;
            context.Log($"{inst}: {history.Count} bars, ATR={s.Atr:F4}, IM_Z窗口={s.ZHistoryCount}点");
        }
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;

        // 预热期：更新指标，不交易
        if (_ctx.IsWarmup)
        {
            s.UpdateAtr(bar);
            if (_factorCache.TryGetValue(bar.InstrumentId, out var facDict) &&
                facDict.TryGetValue(bar.TradingDay, out var im))
            {
                s.PushIntradayMom(im, bar.TradingDay);
            }
            return;
        }

        s.UpdateAtr(bar);

        var pos = _ctx.GetPosition(bar.InstrumentId);
        var hasPosition = pos is not null && pos.Quantity != 0;

        // ── 每日开平仓（日线模式：一根Bar=一个交易日）──

        // 先平昨仓
        if (hasPosition)
        {
            var shouldExit = false;
            string exitReason = "";

            // 止损
            if (pos!.Quantity > 0 && bar.LowDouble <= s.StopLoss)
                { shouldExit = true; exitReason = $"止损 SL@{s.StopLoss:F4}"; }
            else if (pos.Quantity < 0 && bar.HighDouble >= s.StopLoss)
                { shouldExit = true; exitReason = $"止损 SL@{s.StopLoss:F4}"; }
            // 止盈
            else if (pos.Quantity > 0 && s.TakeProfit > 0 && bar.HighDouble >= s.TakeProfit)
                { shouldExit = true; exitReason = $"止盈 TP@{s.TakeProfit:F4}"; }
            else if (pos.Quantity < 0 && s.TakeProfit > 0 && bar.LowDouble <= s.TakeProfit)
                { shouldExit = true; exitReason = $"止盈 TP@{s.TakeProfit:F4}"; }
            // 日末强制平仓
            else
                { shouldExit = true; exitReason = "日末平仓"; }

            if (shouldExit)
            {
                _ctx.ClosePosition(bar.InstrumentId);
                _ctx.Log($"平仓: {bar.InstrumentId} @{bar.CloseDouble:F4} {exitReason}");
                s.ResetTrade();
                hasPosition = false;
            }
        }

        // ── 开仓：用前一日 IntradayMom Z-score 决定方向 ──
        //     LastZScore 保存的是上一个交易日 Push 的值 (前视偏差安全)
        if (!hasPosition && s.IsZReady && s.Atr > 0)
        {
            var prevZ = s.LastZScore;
            var prevRaw = s.LastRaw;

            // ATR 过滤 + 信号过滤
            if (s.Atr / bar.CloseDouble >= 0.001 &&
                Math.Abs(prevZ) > ZEntryThreshold &&
                Math.Abs(prevRaw) > 0.0001)
            {
                var isLong = prevZ > 0;
                var q = PositionSizer.FromAtrStop(bar.CloseDouble, s.Atr, StopAtrMult,
                    _ctx.GetFuture(bar.InstrumentId),
                    (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital),
                    RiskPerTrade, MaxMarginRatio, MaxPosition);

                if (q > 0)
                {
                    if (isLong)
                    {
                        _ctx.MarketBuy(bar.InstrumentId, q, $"IM_Z={prevZ:F2}");
                        s.StopLoss = bar.CloseDouble - StopAtrMult * s.Atr;
                        s.TakeProfit = TakeProfitAtrMult > 0 ? bar.CloseDouble + TakeProfitAtrMult * s.Atr : 0;
                    }
                    else
                    {
                        _ctx.MarketSell(bar.InstrumentId, q, $"IM_Z={prevZ:F2}");
                        s.StopLoss = bar.CloseDouble + StopAtrMult * s.Atr;
                        s.TakeProfit = TakeProfitAtrMult > 0 ? bar.CloseDouble - TakeProfitAtrMult * s.Atr : 0;
                    }
                    s.EntryPrice = bar.CloseDouble;
                    s.Direction = isLong ? "Long" : "Short";
                    _ctx.Log($"入场: {bar.InstrumentId} {s.Direction} @{bar.CloseDouble:F4} IM_Z={prevZ:F2} SL={s.StopLoss:F4} q={q}");
                }
            }
        }

        // ⚠️ Push TODAY's IntradayMom AFTER entry decision (for tomorrow's use)
        if (_factorCache.TryGetValue(bar.InstrumentId, out var facDict2) &&
            facDict2.TryGetValue(bar.TradingDay, out var im2))
        {
            s.PushIntradayMom(im2, bar.TradingDay);
        }
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (!_state.TryGetValue(evt.InstrumentId, out var s)) return;
        if (evt.Type == OrderEventType.Rejected)
        {
            s.ResetTrade();
            _ctx.Log($"订单被拒 #{evt.OrderId} {evt.Message}");
        }
    }

    public void OnEndOfAlgorithm() { }

    // ═══════════════════════════════════════════════════════════════
    // CSV 加载
    // ═══════════════════════════════════════════════════════════════

    private string ResolveCsvPath()
    {
        if (!string.IsNullOrEmpty(FactorCsvPath) && File.Exists(FactorCsvPath))
            return FactorCsvPath;

        // 默认路径
        var candidates = new[]
        {
            "scripts/factor_research/output/intraday_mom_z.csv",
            "../scripts/factor_research/output/intraday_mom_z.csv",
            "../../scripts/factor_research/output/intraday_mom.csv",
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }
        return FactorCsvPath;
    }

    private void LoadFactorCsv(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) return;

        // 解析 header
        var header = lines[0].Split(',');
        var idxInst = Array.IndexOf(header, "instrument_id");
        var idxDate = Array.IndexOf(header, "trading_day");
        var idxIM = Array.IndexOf(header, "IntradayMom");

        if (idxInst < 0 || idxDate < 0 || idxIM < 0)
        {
            _ctx.LogError($"CSV缺少必要列: instrument_id/trading_day/IntradayMom");
            return;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            if (parts.Length <= Math.Max(idxInst, Math.Max(idxDate, idxIM))) continue;

            var inst = parts[idxInst].Trim('"');
            var dateStr = parts[idxDate].Trim('"');
            var imStr = parts[idxIM].Trim('"');

            if (!DateOnly.TryParse(dateStr, out var date)) continue;
            if (!double.TryParse(imStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var im)) continue;

            if (!_factorCache.TryGetValue(inst, out var dict))
            {
                dict = new SortedDictionary<DateOnly, double>();
                _factorCache[inst] = dict;
            }
            dict[date] = im;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // InstrumentState
    // ═══════════════════════════════════════════════════════════════

    private sealed class InstrumentState
    {
        private readonly AtrIndicator _atr;
        private readonly int _zWindow;
        private readonly List<(DateOnly Date, double Value)> _imHistory = new();

        public double Atr;
        public double EntryPrice, StopLoss, TakeProfit;
        public string? Direction;
        public double LastRaw, LastZScore;
        public bool IsZReady => _imHistory.Count >= Math.Min(60, _zWindow);
        public int ZHistoryCount => _imHistory.Count;

        public InstrumentState(int atrPeriod, int zWindow)
        {
            _atr = new AtrIndicator(atrPeriod);
            _zWindow = zWindow;
        }

        public void UpdateAtr(Bar bar)
        {
            _atr.Update(bar);
            if (_atr.IsReady) Atr = _atr.CurrentValue;
        }

        public void PushIntradayMom(double value, DateOnly date)
        {
            _imHistory.Add((date, value));
            // 保持窗口大小
            while (_imHistory.Count > _zWindow + 10)
                _imHistory.RemoveAt(0);

            // 计算滚动 Z-score
            if (_imHistory.Count >= Math.Min(60, _zWindow))
            {
                var recent = _imHistory.TakeLast(_zWindow).Select(x => x.Value).ToList();
                var mean = recent.Average();
                var std = Math.Sqrt(recent.Sum(x => (x - mean) * (x - mean)) / recent.Count);
                LastRaw = value;
                LastZScore = std > 1e-10 ? (value - mean) / std : 0;
            }
        }

        public void ResetTrade()
        {
            EntryPrice = StopLoss = TakeProfit = 0;
            Direction = null;
        }
    }
}
