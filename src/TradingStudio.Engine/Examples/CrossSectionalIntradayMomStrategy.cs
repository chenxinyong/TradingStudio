using System.Globalization;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;
using TradingStudio.Core.Sizing;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine.Examples;

/// <summary>
/// 日内动量因子 — 横截面多品种策略。
///
/// 核心逻辑（Python IC分析验证）：
///   IntradayMom = (Close_30min - Open) / Open 在 72品种横截面上 IC_IR=0.96。
///   每天按 IntradayMom Z-score 排名，做多 TopN，做空 BottomN。
///
/// 交易规则：
///   1. 每日收盘前，按前日 IntradayMom Z-score 对所有品种排名
///   2. 做多 Z-score 最高的 TopN 个品种（等权）
///   3. 做空 Z-score 最低的 TopN 个品种（等权）
///   4. 次日收盘平仓，循环
///
/// 风险控制：
///   - 单品种 ATR 止损/止盈
///   - 总保证金上限控制
///   - 最多同时持仓 2×TopN 个品种
/// </summary>
public class CrossSectionalIntradayMomStrategy : IStrategy
{
    [StrategyParameter(Description = "做多/做空品种数", DefaultValue = 5, Min = 1, Max = 20, Category = "Portfolio")]
    public int TopN { get; set; } = 5;

    [StrategyParameter(Description = "Z-score 滚动窗口天数", DefaultValue = 252, Min = 60, Max = 504, Category = "Entry")]
    public int ZWindowDays { get; set; } = 252;

    [StrategyParameter(Description = "ATR周期", DefaultValue = 20, Min = 10, Max = 40, Category = "Risk")]
    public int AtrPeriod { get; set; } = 20;

    [StrategyParameter(Description = "止损ATR倍数", DefaultValue = 2.0, Min = 1.0, Max = 5.0, Category = "Risk")]
    public double StopAtrMult { get; set; } = 2.0;

    [StrategyParameter(Description = "止盈ATR倍数 (0=关闭)", DefaultValue = 3.0, Min = 0, Max = 10.0, Category = "Risk")]
    public double TakeProfitAtrMult { get; set; } = 3.0;

    [StrategyParameter(Description = "单品种单笔风险占比", DefaultValue = 0.02, Min = 0.005, Max = 0.05, Category = "Position")]
    public double RiskPerTrade { get; set; } = 0.02;

    [StrategyParameter(Description = "最大保证金占比", DefaultValue = 0.50, Min = 0.20, Max = 0.80, Category = "Position")]
    public double MaxMarginRatio { get; set; } = 0.50;

    [StrategyParameter(Description = "单品种最大手数", DefaultValue = 2, Min = 1, Max = 10, Category = "Position")]
    public int MaxPosition { get; set; } = 2;

    [StrategyParameter(Description = "因子CSV路径", DefaultValue = "", Category = "Data")]
    public string FactorCsvPath { get; set; } = "";

    public string Name => "横截面日内动量(TopN)";

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, InstrumentState> _state = new();
    private readonly Dictionary<string, SortedDictionary<DateOnly, double>> _factorCache = new();

    // 日切跟踪
    private DateOnly _lastTradingDay;
    private bool _firstDay = true;

    public void Initialize(StrategyContext context)
    {
        _ctx = context;

        var csvPath = ResolveCsvPath();
        if (!File.Exists(csvPath))
        {
            context.LogError($"因子CSV不存在: {csvPath}");
            return;
        }
        LoadFactorCsv(csvPath);
        context.Log($"横截面策略: {_factorCache.Count} 品种因子, TopN={TopN}");

        foreach (var inst in context.SubscribedInstruments)
        {
            var history = context.GetBarHistory(inst);
            var s = new InstrumentState(AtrPeriod, ZWindowDays);
            foreach (var bar in history)
            {
                s.UpdateAtr(bar);
                if (_factorCache.TryGetValue(inst, out var dict) && dict.TryGetValue(bar.TradingDay, out var im))
                    s.PushIntradayMom(im);
            }
            _state[inst] = s;
        }
        context.Log($"初始化: {_state.Count} 品种, 历史预热完成");
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;

        s.UpdateAtr(bar);

        if (_ctx.IsWarmup)
        {
            // 预热期: 只推进因子窗口, 不交易
            if (_factorCache.TryGetValue(bar.InstrumentId, out var facDictW) &&
                facDictW.TryGetValue(bar.TradingDay, out var imW))
            {
                s.PushIntradayMom(imW);
            }
            return;
        }

        // ── 日切检测：新交易日到来 → 平昨仓 + 开今仓 ──
        //    注意: RebalancePortfolio 必须在 PushIntradayMom 之前执行,
        //    因为 Rebalance 使用前一天已保存的 PrevZScore。
        if (bar.TradingDay != _lastTradingDay)
        {
            if (!_firstDay)
            {
                CloseAllPositions();
                RebalancePortfolio();
            }
            _firstDay = false;
            _lastTradingDay = bar.TradingDay;
        }

        // Feed 当日的 IntradayMom → 更新 LastZScore/PrevZScore (用于下一个交易日)
        if (_factorCache.TryGetValue(bar.InstrumentId, out var facDict) &&
            facDict.TryGetValue(bar.TradingDay, out var im))
        {
            s.PushIntradayMom(im);
        }

        // ── 止损/止盈检查 (日内退出, 仅当 StopAtrMult>0 时启用) ──
        var pos = _ctx.GetPosition(bar.InstrumentId);
        if (pos is not null && pos.Quantity != 0 && s.EntryPrice > 0)
        {
            bool shouldExit = false;
            string reason = "";

            if (pos.Quantity > 0) // Long
            {
                if (bar.LowDouble <= s.StopLoss)
                    { shouldExit = true; reason = "SL"; }
                else if (s.TakeProfit > 0 && bar.HighDouble >= s.TakeProfit)
                    { shouldExit = true; reason = "TP"; }
            }
            else // Short
            {
                if (bar.HighDouble >= s.StopLoss)
                    { shouldExit = true; reason = "SL"; }
                else if (s.TakeProfit > 0 && bar.LowDouble <= s.TakeProfit)
                    { shouldExit = true; reason = "TP"; }
            }

            if (shouldExit)
            {
                _ctx.ClosePosition(bar.InstrumentId);
                _ctx.Log($"止损/盈: {bar.InstrumentId} {reason} @{bar.CloseDouble:F4}");
                s.ResetTrade();
            }
        }
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (evt.Type == OrderEventType.Rejected && _state.TryGetValue(evt.InstrumentId, out var s))
        {
            s.ResetTrade();
            _ctx.Log($"订单被拒: {evt.InstrumentId} #{evt.OrderId}");
        }
    }

    public void OnEndOfAlgorithm() { }

    // ═══════════════════════════════════════════════════════════════
    // 核心逻辑
    // ═══════════════════════════════════════════════════════════════

    private void CloseAllPositions()
    {
        foreach (var pos in _ctx.Positions)
        {
            if (pos.Quantity != 0)
            {
                _ctx.ClosePosition(pos.InstrumentId);
                if (_state.TryGetValue(pos.InstrumentId, out var s))
                    s.ResetTrade();
            }
        }
    }

    private void RebalancePortfolio()
    {
        // 收集所有品种的 IntradayMom Z-score
        var rankings = new List<(string Inst, double Z, double Raw, double Atr, double Price)>();
        foreach (var (inst, s) in _state)
        {
            if (!s.IsZReady || s.Atr <= 0) continue;
            // 排除持仓中品种（已被止损/盈平掉的会在此处再次入选）
            rankings.Add((inst, s.LastZScore, s.LastRaw, s.Atr, s.LastPrice));
        }

        if (rankings.Count < TopN * 2) return;

        // 按 Z-score 降序排列
        rankings.Sort((a, b) => b.Z.CompareTo(a.Z));

        var equity = (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital);
        int opened = 0;

        // 做多 TopN (Z-score 最高)
        for (int i = 0; i < Math.Min(TopN, rankings.Count); i++)
        {
            var (inst, z, raw, atr, price) = rankings[i];
            if (atr / price < 0.001) continue; // 波动率过低

            var future = _ctx.GetFuture(inst);
            var q = PositionSizer.FromAtrStop(price, atr, StopAtrMult,
                future, equity, RiskPerTrade, MaxMarginRatio, MaxPosition);
            if (q <= 0) continue;

            _ctx.MarketBuy(inst, q, $"L_Z={z:F2}");
            var s = _state[inst];
            s.EntryPrice = price;
            s.StopLoss = price - StopAtrMult * atr;
            s.TakeProfit = TakeProfitAtrMult > 0 ? price + TakeProfitAtrMult * atr : 0;
            s.Direction = "Long";
            opened++;
        }

        int longCount = 0, shortCount = 0;

        // 做空 BottomN (Z-score 最低)
        for (int i = rankings.Count - 1; i >= Math.Max(0, rankings.Count - TopN); i--)
        {
            var (inst, z, raw, atr, price) = rankings[i];
            if (atr / price < 0.001) continue;

            var future = _ctx.GetFuture(inst);
            var q = PositionSizer.FromAtrStop(price, atr, StopAtrMult,
                future, equity, RiskPerTrade, MaxMarginRatio, MaxPosition);
            if (q <= 0) continue;

            _ctx.MarketSell(inst, q, $"S_Z={z:F2}");
            Console.Error.WriteLine($"[XS-SHORT] {inst} Z={z:F2} q={q} price={price:F2}");
            var s = _state[inst];
            s.EntryPrice = price;
            s.StopLoss = price + StopAtrMult * atr;
            s.TakeProfit = TakeProfitAtrMult > 0 ? price - TakeProfitAtrMult * atr : 0;
            s.Direction = "Short";
            opened++;
        }

        if (opened > 0)
            _ctx.Log($"调仓: {rankings.Count}品种 → Long x{Math.Min(TopN, rankings.Count)} Short x{Math.Min(TopN, rankings.Count)} → 实际开仓 {opened}");
        else
            Console.Error.WriteLine($"[XS-DIAG] NO_TRADES: rankings={rankings.Count} topN={TopN}");
    }

    // ═══════════════════════════════════════════════════════════════
    // CSV 加载 (与 IntradayMomentumStrategy 相同)
    // ═══════════════════════════════════════════════════════════════

    private string ResolveCsvPath()
    {
        if (!string.IsNullOrEmpty(FactorCsvPath) && File.Exists(FactorCsvPath))
            return FactorCsvPath;
        var candidates = new[] {
            "scripts/factor_research/output/intraday_mom_z.csv",
            "../scripts/factor_research/output/intraday_mom_z.csv",
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return Path.GetFullPath(c);
        return FactorCsvPath;
    }

    private void LoadFactorCsv(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) return;
        var header = lines[0].Split(',');
        int iInst = Array.IndexOf(header, "instrument_id");
        int iDate = Array.IndexOf(header, "trading_day");
        int iIM = Array.IndexOf(header, "IntradayMom");
        if (iInst < 0 || iDate < 0 || iIM < 0) return;

        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            if (parts.Length <= Math.Max(iInst, Math.Max(iDate, iIM))) continue;
            var inst = parts[iInst].Trim('"');
            if (!DateOnly.TryParse(parts[iDate].Trim('"'), out var date)) continue;
            if (!double.TryParse(parts[iIM].Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var im)) continue;
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
        private readonly List<double> _imHistory = new();

        public double Atr, EntryPrice, StopLoss, TakeProfit, LastPrice;
        public string? Direction;
        public double LastRaw, LastZScore;
        public bool IsZReady => _imHistory.Count >= Math.Min(60, _zWindow);

        public InstrumentState(int atrPeriod, int zWindow)
        {
            _atr = new AtrIndicator(atrPeriod);
            _zWindow = zWindow;
        }

        public void UpdateAtr(Bar bar)
        {
            _atr.Update(bar);
            if (_atr.IsReady) Atr = _atr.CurrentValue;
            LastPrice = bar.CloseDouble;
        }

        public void PushIntradayMom(double value)
        {
            _imHistory.Add(value);
            while (_imHistory.Count > _zWindow + 10)
                _imHistory.RemoveAt(0);
            if (_imHistory.Count >= Math.Min(60, _zWindow))
            {
                var recent = _imHistory.TakeLast(_zWindow).ToList();
                double mean = recent.Average();
                double sumSq = recent.Sum(x => (x - mean) * (x - mean));
                double std = Math.Sqrt(sumSq / recent.Count);
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
