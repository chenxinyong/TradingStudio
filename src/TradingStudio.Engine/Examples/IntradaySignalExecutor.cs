using System.Globalization;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Sizing;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine.Examples;

/// <summary>
/// 日内信号执行器 — 从 CSV 读取预计算的横截面 IntradayMom 信号，
/// 在 09:30 入场，14:45 出场。
///
/// 信号来源：Python pre-computed from bars_15min
///   IntradayMom = (close_09:15 - open_09:00) / open_09:00
///   横截面排名 → Top 5 Long + Bottom 5 Short
///   入场: 09:30 bar open  → 出场: 14:45 bar close
/// </summary>
public class IntradaySignalExecutor : IStrategy
{
    [StrategyParameter(Description = "信号CSV路径", DefaultValue = "", Category = "Data")]
    public string SignalCsvPath { get; set; } = "";

    [StrategyParameter(Description = "单品种最大手数", DefaultValue = 2, Min = 1, Max = 10, Category = "Position")]
    public int MaxPosition { get; set; } = 2;

    public string Name => "日内信号执行器(IntradayMom)";

    private StrategyContext _ctx = null!;
    // (tradingDay, instrumentId) → (direction: "LONG"/"SHORT")
    private readonly Dictionary<(DateOnly, string), string> _signals = new();
    private DateOnly _lastSignalDay;
    private bool _positionsOpen;

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        LoadSignals();
        context.Log($"日内执行器: {_signals.Count} 信号加载, {context.SubscribedInstruments.Count} 品种订阅");
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (_ctx.IsWarmup) return;

        var time = bar.BarTime;
        var day = bar.TradingDay;

        // ── 09:30: 进入当日仓位 ──
        if (time.Hour == 9 && time.Minute == 30 && !_positionsOpen)
        {
            EnterPositions(day);
            _positionsOpen = true;
            _lastSignalDay = day;
        }

        // ── 14:45: 平仓 ──
        if (time.Hour == 14 && time.Minute == 45 && _positionsOpen)
        {
            CloseAllPositions();
            _positionsOpen = false;
        }
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (evt.Type == OrderEventType.Rejected)
            Console.Error.WriteLine($"[EXEC] REJECTED: {evt.InstrumentId} #{evt.OrderId} {evt.Message}");
    }

    public void OnEndOfAlgorithm() { }

    private void EnterPositions(DateOnly day)
    {
        int count = 0;
        foreach (var inst in _ctx.SubscribedInstruments)
        {
            if (!_signals.TryGetValue((day, inst), out var dir)) continue;

            int q = 1; // 固定1手/品种（简化，避免PositionSizer依赖）
            if (dir == "LONG")
            {
                _ctx.MarketBuy(inst, q, $"IM");
                count++;
            }
            else if (dir == "SHORT")
            {
                _ctx.MarketSell(inst, q, $"IM");
                count++;
            }
        }
        if (count > 0)
            Console.Error.WriteLine($"[EXEC] {day:yyyy-MM-dd}: {count} positions opened");
    }

    private void CloseAllPositions()
    {
        foreach (var pos in _ctx.Positions)
        {
            if (pos.Quantity != 0)
                _ctx.ClosePosition(pos.InstrumentId);
        }
    }

    private void LoadSignals()
    {
        var path = ResolvePath();
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"[EXEC] Signal CSV not found: {path}");
            return;
        }
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) return;
        var hdr = lines[0].Split(',');
        int iDay = Array.IndexOf(hdr, "trading_day");
        int iInst = Array.IndexOf(hdr, "instrument_id");
        int iDir = Array.IndexOf(hdr, "direction");
        if (iDay < 0 || iInst < 0 || iDir < 0) return;

        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            if (parts.Length <= Math.Max(iDay, Math.Max(iInst, iDir))) continue;
            if (!DateOnly.TryParse(parts[iDay].Trim('"'), out var date)) continue;
            var inst = parts[iInst].Trim('"');
            var dir = parts[iDir].Trim('"');
            _signals[(date, inst)] = dir;
        }
    }

    private string ResolvePath()
    {
        if (!string.IsNullOrEmpty(SignalCsvPath) && File.Exists(SignalCsvPath))
            return SignalCsvPath;
        var c = new[] { "scripts/factor_research/output/intraday_signals.csv",
            "../scripts/factor_research/output/intraday_signals.csv" };
        foreach (var p in c) if (File.Exists(p)) return Path.GetFullPath(p);
        return SignalCsvPath;
    }
}
