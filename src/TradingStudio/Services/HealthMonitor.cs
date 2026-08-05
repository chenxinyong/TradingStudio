using System.Text.Json;

namespace TradingStudio.Services;

/// <summary>
/// 健康监控 v2 — 写 health.json + 每日盈亏追踪。
/// </summary>
public class HealthMonitor
{
    private readonly string _path;
    private readonly List<EquityPoint> _equityCurve = new();
    private decimal _sessionStartEquity;
    private readonly object _lock = new();

    public HealthMonitor(string path = "health.json") => _path = path;

    /// <summary>每日起始权益（会话开始时由 EngineHost 设置）</summary>
    public void SetSessionStartEquity(decimal equity)
    {
        lock (_lock) { _sessionStartEquity = equity; _equityCurve.Clear(); }
    }

    /// <summary>记录权益快照（每N分钟调用一次）</summary>
    public void RecordEquity(decimal equity)
    {
        lock (_lock)
        {
            _equityCurve.Add(new EquityPoint(DateTime.Now, equity));
            if (_equityCurve.Count > 500) _equityCurve.RemoveAt(0); // 保留最近500点
        }
    }

    public IReadOnlyList<EquityPoint> EquityCurve { get { lock (_lock) return _equityCurve.ToList(); } }

    /// <summary>当日盈亏 (bps)</summary>
    public decimal DailyPnL => _sessionStartEquity > 0
        ? (GetCurrentEquity() - _sessionStartEquity) / _sessionStartEquity
        : 0;

    private decimal GetCurrentEquity()
    {
        lock (_lock) return _equityCurve.Count > 0 ? _equityCurve[^1].Equity : _sessionStartEquity;
    }

    public record EquityPoint(DateTime Time, decimal Equity);

    public void Update(
        string status,
        long quoteCount, long barCount, long csvCount, long reconnectCount,
        string? session, DateTime? lastConnect, DateTime? lastQuote, DateTime? lastHealth,
        decimal? equity = null, int? positions = null)
    {
        if (equity.HasValue) RecordEquity(equity.Value);

        var currentEquity = equity ?? GetCurrentEquity();
        var dailyPnL = _sessionStartEquity > 0
            ? (currentEquity - _sessionStartEquity) / _sessionStartEquity : 0m;

        var h = new
        {
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            status, session,
            quotes = quoteCount, bars = barCount, csv = csvCount, reconnects = reconnectCount,
            equity = Math.Round(currentEquity, 2),
            dailyPnL = Math.Round(dailyPnL * 100, 4),  // 百分比
            sessionStartEquity = Math.Round(_sessionStartEquity, 2),
            positions = positions ?? 0,
            lastConnect = lastConnect?.ToString("yyyy-MM-dd HH:mm:ss"),
            lastQuote = lastQuote?.ToString("yyyy-MM-dd HH:mm:ss"),
            lastHealth = lastHealth?.ToString("yyyy-MM-dd HH:mm:ss"),
            uptime = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).ToString(@"d\.hh\:mm\:ss")
        };

        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(h, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        }
        catch { }
    }
}
