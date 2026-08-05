using System.Globalization;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;
using TradingStudio.Core.Sizing;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine.Examples;

/// <summary>
/// 日内信号执行器 v2 — 流动性过滤 + ATR仓位管理 + 低滑点市价单。
///
/// 信号来源：Python pre-computed from bars_15min
///   IntradayMom = (close_09:15 - open_09:00) / open_09:00
///   横截面排名 → Top 5 Long + Bottom 5 Short
///   入场: 09:30 限价单(接近entry_price) → 出场: 14:45 市价平仓
///
/// 改进 (v2):
///   - 限价单替代市价单, 减少滑点
///   - 成交量过滤, 跳过流动性不足的品种
///   - ATR动态仓位管理
/// </summary>
public class IntradaySignalExecutor : IStrategy
{
    [StrategyParameter(Description = "信号CSV路径", DefaultValue = "", Category = "Data")]
    public string SignalCsvPath { get; set; } = "";

    [StrategyParameter(Description = "单品种最大手数", DefaultValue = 2, Min = 1, Max = 10, Category = "Position")]
    public int MaxPosition { get; set; } = 2;

    [StrategyParameter(Description = "单笔风险占比", DefaultValue = 0.01, Min = 0.005, Max = 0.03, Category = "Position")]
    public double RiskPerTrade { get; set; } = 0.01;

    [StrategyParameter(Description = "最低日均成交量(手)", DefaultValue = 5000, Min = 0, Max = 100000, Category = "Filter")]
    public int MinDailyVolume { get; set; } = 5000;

    [StrategyParameter(Description = "ATR周期", DefaultValue = 20, Min = 10, Max = 40, Category = "Risk")]
    public int AtrPeriod { get; set; } = 20;

    public string Name => "日内信号执行器v2(ML复合+流动性)";

    private StrategyContext _ctx = null!;
    private readonly Dictionary<(DateOnly, string), SignalInfo> _signals = new();
    private readonly Dictionary<string, AtrIndicator> _atrs = new();
    private readonly Dictionary<string, double> _lastPrices = new();
    private readonly Dictionary<string, long> _dailyVolumes = new();
    private DateOnly _currentDay;

    private struct SignalInfo
    {
        public string Direction;
        public double EntryPrice;
        public double ExitPrice;
    }

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        LoadSignals();
        foreach (var inst in context.SubscribedInstruments)
            _atrs[inst] = new AtrIndicator(AtrPeriod);
        context.Log($"日内执行器v2: {_signals.Count}信号, {context.SubscribedInstruments.Count}品种, 限价单模式");
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (_ctx.IsWarmup) return;

        var inst = bar.InstrumentId;
        var time = bar.BarTime;
        var day = bar.TradingDay;

        // 更新ATR
        if (_atrs.TryGetValue(inst, out var atr))
            atr.Update(bar);

        // Track last price + daily volume
        _lastPrices[inst] = bar.CloseDouble;

        // 日切: 重置成交量累计
        if (day != _currentDay)
        {
            _currentDay = day;
            _dailyVolumes.Clear();
        }
        _dailyVolumes.TryGetValue(inst, out var prevVol);
        _dailyVolumes[inst] = prevVol + bar.Volume;

        // ── 09:00~09:15: 不做任何事, 等待IntradayMom计算 ──
        if (time.Hour == 9 && time.Minute <= 15) return;

        // ── 09:30: 限价单入场 ──
        if (time.Hour == 9 && time.Minute == 30)
        {
            EnterPositions(day);
        }

        // ── 14:45: 市价平仓 ──
        if (time.Hour == 14 && time.Minute == 45)
        {
            CloseAllPositions();
        }
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (evt.Type == OrderEventType.Rejected)
            _ctx.Log($"订单被拒: {evt.InstrumentId} #{evt.OrderId} {evt.Message}");
    }

    public void OnEndOfAlgorithm() { }

    // ═══════════════════════════════════════════════════════════════

    private void EnterPositions(DateOnly day)
    {
        var equity = (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital);
        int count = 0, skippedVol = 0, skippedLiq = 0;

        foreach (var inst in _ctx.SubscribedInstruments)
        {
            if (!_signals.TryGetValue((day, inst), out var sig)) continue;

            // 流动性过滤: 当日开盘15min成交量
            _dailyVolumes.TryGetValue(inst, out var vol);
            if (vol < MinDailyVolume / 30)  // 日均/30 ≈ 每15min预期成交量
            {
                skippedVol++;
                continue;
            }

            // ATR未就绪则跳过
            if (!_atrs.TryGetValue(inst, out var atr) || !atr.IsReady)
                continue;
            var currentAtr = atr.CurrentValue;

            // 仓位计算
            var future = _ctx.GetFuture(inst);
            _lastPrices.TryGetValue(inst, out var price);
            if (price <= 0) continue;

            var q = PositionSizer.FromAtrStop(price, currentAtr, 2.0,
                future, equity, RiskPerTrade, 0.35, MaxPosition);
            if (q <= 0)
            {
                skippedLiq++;
                continue;
            }

            // 市价单 + ATR仓位管理 (SlippageAtrFactor=0.02控制滑点)
            if (sig.Direction == "LONG")
                _ctx.MarketBuy(inst, q, $"ML");
            else
                _ctx.MarketSell(inst, q, $"ML");
            count++;
        }

        if (count > 0 || skippedVol > 0)
            _ctx.Log($"{day:yyyy-MM-dd}: {count}开仓, 跳过{skippedVol}低量+{skippedLiq}仓位不足");
    }

    private void CloseAllPositions()
    {
        int count = 0;
        foreach (var pos in _ctx.Positions)
        {
            if (pos.Quantity != 0)
            {
                _ctx.ClosePosition(pos.InstrumentId, "EOD");
                count++;
            }
        }
        if (count > 0)
            _ctx.Log($"平仓: {count} 个品种");
    }

    // ═══════════════════════════════════════════════════════════════

    private void LoadSignals()
    {
        var path = ResolvePath();
        if (!File.Exists(path))
        {
            _ctx.LogError($"信号CSV不存在: {path}");
            return;
        }
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) return;
        var hdr = lines[0].Split(',');
        int iDay = Array.IndexOf(hdr, "trading_day");
        int iInst = Array.IndexOf(hdr, "instrument_id");
        int iDir = Array.IndexOf(hdr, "direction");
        int iEntry = Array.IndexOf(hdr, "entry");
        int iExit = Array.IndexOf(hdr, "exit");

        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            if (parts.Length <= Math.Max(iDay, Math.Max(iInst, iDir))) continue;
            if (!DateOnly.TryParse(parts[iDay].Trim('"'), out var date)) continue;
            var inst = parts[iInst].Trim('"');
            var dir = parts[iDir].Trim('"');
            double entry = 0, exit = 0;
            if (iEntry >= 0 && iEntry < parts.Length)
                double.TryParse(parts[iEntry].Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out entry);
            if (iExit >= 0 && iExit < parts.Length)
                double.TryParse(parts[iExit].Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out exit);

            _signals[(date, inst)] = new SignalInfo { Direction = dir, EntryPrice = entry, ExitPrice = exit };
        }
    }

    private string ResolvePath()
    {
        if (!string.IsNullOrEmpty(SignalCsvPath) && File.Exists(SignalCsvPath))
            return SignalCsvPath;
        foreach (var p in new[] { "scripts/factor_research/output/intraday_signals.csv",
            "../scripts/factor_research/output/intraday_signals.csv" })
            if (File.Exists(p)) return Path.GetFullPath(p);
        return SignalCsvPath;
    }
}
