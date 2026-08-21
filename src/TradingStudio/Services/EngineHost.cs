using TradingStudio.Engine;
using TradingStudio.Live;
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
    private readonly CtpTraderBridge? _trader;
    private int _crashCount;

    public EngineHost(TradingEngine engine, SessionScheduler session,
                      HealthMonitor health, PortfolioManager portfolio, Serilog.ILogger log,
                      CtpTraderBridge? trader = null)
    {
        _engine = engine;
        _session = session;
        _health = health;
        _portfolio = portfolio;
        _log = log.ForContext<EngineHost>();
        _trader = trader;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_session.IsInSession())
            {
                _log.Information("Waiting for next trading session...");
                _health.Update("Idle", 0, 0, 0, 0, "休市", null, null, null);

                // 分片等待进入交易时段：每片最长 30s，逐片重算剩余时间。
                // 不能用单次长 Task.Delay —— 系统休眠会冻结计时器，导致错过开盘
                // （8/18 日盘未启动即此因：机器在 02:30-12:29 休眠，6h 计时器未触发）。
                // 分片在正常时仍能精确到秒级；休眠唤醒后下一片立即重算并恢复。
                while (!_session.IsInSession() && !ct.IsCancellationRequested)
                {
                    var remaining = _session.WaitUntilNextSession();
                    var slice = remaining > TimeSpan.FromSeconds(30)
                        ? TimeSpan.FromSeconds(30)
                        : remaining;
                    if (slice <= TimeSpan.Zero) continue;
                    try { await Task.Delay(slice, ct); }
                    catch (OperationCanceledException) { break; }
                }
                if (ct.IsCancellationRequested) break;
            }

            var sessionName = _session.SessionName();
            var sessionStartEquity = GetPortfolioEquity();
            _health.SetSessionStartEquity(sessionStartEquity);
            _crashCount = 0;
            _log.Information("Session [{Session}] starting, Equity={Equity:C}", sessionName, sessionStartEquity);

            // 会话开始：确保交易通道就绪。若盘前连接被前置机踢下线、重连退避已涨到 300s，
            // 这里打断退避并立即重连，避免开盘后订单因「CTP not ready」被拒。
            _trader?.EnsureConnected();

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

                // 会话运行期间挂「交易通道就绪看门狗」：若 CTP 交易桥接长时间未就绪，
                // 打 Error 告警，避免「订单被静默拒绝」持续几小时才被发现。
                using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                var watchdog = WatchTraderReadinessAsync(watchdogCts.Token);
                try
                {
                    await _engine.RunAsync(sessionCts.Token);
                }
                finally
                {
                    watchdogCts.Cancel();
                    try { await watchdog; } catch { /* 看门狗自身异常不影响主循环 */ }
                }

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

    /// <summary>
    /// 交易通道就绪看门狗：会话运行期间每 30s 检查一次 CTP 交易桥接的 IsReady。
    /// 若连续未就绪 ≥ 5 分钟，打 Error 告警并更新 health 状态，之后每 5 分钟重报一次。
    /// </summary>
    private async Task WatchTraderReadinessAsync(CancellationToken ct)
    {
        var trader = _trader;
        if (trader == null) return;

        DateTimeOffset? downSince = null;
        DateTimeOffset? lastAlert = null;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }

            if (trader.IsReady)
            {
                if (downSince != null)
                    _log.Information("CTP 交易通道已恢复就绪（此前未就绪 {Min:F1} 分钟）",
                        (DateTimeOffset.UtcNow - downSince.Value).TotalMinutes);
                downSince = null;
                continue;
            }

            downSince ??= DateTimeOffset.UtcNow;
            var down = DateTimeOffset.UtcNow - downSince.Value;
            if (down < TimeSpan.FromMinutes(5)) continue;
            if (lastAlert != null && DateTimeOffset.UtcNow - lastAlert.Value < TimeSpan.FromMinutes(5)) continue;

            _log.Error("⚠️ CTP 交易通道未就绪已持续 {Min:F0} 分钟 — 会话中所有新订单将被拒绝，请检查交易前置机连接", down.TotalMinutes);
            _health.Update("TraderDown", 0, 0, 0, 0, _session.SessionName(), null, null, DateTime.Now);
            lastAlert = DateTimeOffset.UtcNow;
        }
    }

    private decimal GetPortfolioEquity() => _portfolio.Equity;
    private int GetTradeCount() => _portfolio.TradeHistory.Count;
    private int GetPositionCount() => _portfolio.AllPositions.Count(p => p.Quantity != 0);
}
