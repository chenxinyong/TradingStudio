using TradingStudio.Engine;
using Serilog;

namespace TradingStudio.Services;

/// <summary>
/// 实盘引擎宿主 v2 — 7x24 自愈循环 + 会话摘要 + 指数退避。
/// </summary>
public class EngineHost : BackgroundService
{
    private readonly TradingEngine _engine;
    private readonly SessionScheduler _session;
    private readonly HealthMonitor _health;
    private readonly PortfolioManager _portfolio;
    private readonly Serilog.ILogger _log;
    private int _crashCount;

    public EngineHost(TradingEngine engine, SessionScheduler session,
                      HealthMonitor health, PortfolioManager portfolio, Serilog.ILogger log)
    {
        _engine = engine;
        _session = session;
        _health = health;
        _portfolio = portfolio;
        _log = log.ForContext<EngineHost>();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_session.IsInSession())
            {
                _log.Information("Waiting for next trading session...");
                _health.Update("Idle", 0, 0, 0, 0, "休市", null, null, null);
                var wait = _session.WaitUntilNextSession();
                try { await Task.Delay(wait, ct); }
                catch (OperationCanceledException) { break; }
            }

            var sessionName = _session.SessionName();
            var sessionStartEquity = GetPortfolioEquity();
            _health.SetSessionStartEquity(sessionStartEquity);
            _crashCount = 0;
            _log.Information("Session [{Session}] starting, Equity={Equity:C}", sessionName, sessionStartEquity);

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sessionEnd = _session.GetSessionEndTime();
            if (sessionEnd.HasValue)
            {
                var remaining = sessionEnd.Value - SessionScheduler.BeijingNow;
                if (remaining > TimeSpan.Zero)
                {
                    _log.Information("Session ends at {End} ({Remaining})", sessionEnd.Value.ToString("HH:mm"),
                        remaining.TotalHours >= 1 ? $"{remaining.TotalHours:F0}h" : $"{remaining.TotalMinutes:F0}m");
                    sessionCts.CancelAfter(remaining);
                }
            }

            try
            {
                _health.Update("Running", 0, 0, 0, 0, sessionName, DateTime.Now, null, null,
                    equity: sessionStartEquity);
                await _engine.RunAsync(sessionCts.Token);

                // ── 会话正常结束 → 打印摘要 ──
                var endEquity = GetPortfolioEquity();
                var pnl = endEquity - sessionStartEquity;
                var pnlPct = sessionStartEquity > 0 ? pnl / sessionStartEquity : 0;
                var tradeCount = GetTradeCount();
                _log.Information("═══ Session [{Session}] SUMMARY ═══", sessionName);
                _log.Information("  Start Equity: {Start:C}", sessionStartEquity);
                _log.Information("  End Equity:   {End:C}", endEquity);
                _log.Information("  PnL:          {PnL:C} ({Pct:P2})", pnl, pnlPct);
                _log.Information("  Trades:       {Trades}", tradeCount);
                _log.Information("══════════════════════════════════");
                _health.Update("Idle", 0, 0, 0, 0, "休市", null, null, null,
                    equity: endEquity, positions: GetPositionCount());
            }
            catch (OperationCanceledException) when (sessionCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _log.Information("Session ended [{Session}]: scheduled close", sessionName);
                LogSessionEnd(sessionName, sessionStartEquity);
                _health.Update("Idle", 0, 0, 0, 0, "休市", null, null, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _crashCount++;

                // 指数退避: 5s, 10s, 20s, 40s, ..., cap 300s
                var delay = Math.Min(300, 5 * (int)Math.Pow(2, Math.Min(_crashCount - 1, 6)));
                _log.Error(ex, "Engine crashed (x{Crash}) — retrying in {Delay}s...",
                    _crashCount, delay);
                _health.Update("Crashed", 0, 0, 0, (long)_crashCount, sessionName, null, null, DateTime.Now);

                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        _log.Information("EngineHost stopped");
    }

    private void LogSessionEnd(string sessionName, decimal startEquity)
    {
        var endEquity = GetPortfolioEquity();
        var pnl = endEquity - startEquity;
        _log.Information("═══ Session [{Session}] END (scheduled) ═══", sessionName);
        _log.Information("  PnL: {PnL:C} ({Pct:P2})  Trades: {Trades}",
            pnl, startEquity > 0 ? pnl / startEquity : 0, GetTradeCount());
        _log.Information("══════════════════════════════════════");
    }

    private decimal GetPortfolioEquity() => _portfolio.Equity;
    private int GetTradeCount() => _portfolio.TradeHistory.Count;
    private int GetPositionCount() => _portfolio.AllPositions.Count(p => p.Quantity != 0);
}
