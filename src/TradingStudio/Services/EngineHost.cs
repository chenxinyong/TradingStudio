using TradingStudio.Engine;
using Serilog;

namespace TradingStudio.Services;

/// <summary>
/// 实盘引擎宿主 — 7x24 自愈循环。
/// 引擎崩溃 → 30s 重试；交易时段外自动休眠。
/// </summary>
public class EngineHost : BackgroundService
{
    private readonly TradingEngine _engine;
    private readonly SessionScheduler _session;
    private readonly HealthMonitor _health;
    private readonly Serilog.ILogger _log;

    public EngineHost(TradingEngine engine, SessionScheduler session,
                      HealthMonitor health, Serilog.ILogger log)
    {
        _engine = engine;
        _session = session;
        _health = health;
        _log = log.ForContext<EngineHost>();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // ── 如果已在交易时段，直接启动；否则等待 ──
            if (!_session.IsInSession())
            {
                _log.Information("Waiting for next trading session...");
                _health.Update("Idle", 0, 0, 0, 0, "休市", null, null, null);

                var wait = _session.WaitUntilNextSession();
                try { await Task.Delay(wait, ct); }
                catch (OperationCanceledException) { break; }
            }

            var sessionName = _session.SessionName();
            _log.Information("Session starting: {Session}", sessionName);

            // ── 运行引擎（会话结束自动停止）──
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sessionEnd = _session.GetSessionEndTime();
            if (sessionEnd.HasValue)
            {
                var remaining = sessionEnd.Value - SessionScheduler.BeijingNow;
                if (remaining > TimeSpan.Zero)
                {
                    _log.Information("Session ends at {End} (in {Remaining})", sessionEnd.Value.ToString("HH:mm"), remaining);
                    sessionCts.CancelAfter(remaining);
                }
            }

            try
            {
                _health.Update("Running", 0, 0, 0, 0, sessionName, DateTime.Now, null, null);
                await _engine.RunAsync(sessionCts.Token);
            }
            catch (OperationCanceledException) when (sessionCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _log.Information("Session ended (scheduled): {Session}", sessionName);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Engine crashed — retrying in 30s...");
                _health.Update("Crashed", 0, 0, 0, 0, sessionName, null, null, DateTime.Now);

                try { await Task.Delay(30_000, ct); }
                catch (OperationCanceledException) { break; }
            }

            // ── 时段结束 ──
            _log.Information("Session ended: {Session}", sessionName);
            _health.Update("Idle", 0, 0, 0, 0, "休市", null, null, null);
        }

        _log.Information("EngineHost stopped");
    }
}
