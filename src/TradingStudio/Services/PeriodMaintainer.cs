using TradingStudio.Core.Storage;
using TradingStudio.Data.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TradingStudio.Services;

/// <summary>
/// 多周期表自动维护 — 后台定时从 bars_1min 刷新 5min/15min/day/week。
/// 交易时段内每 5 分钟增量更新，收盘后兜底全刷。
/// 云端部署时自动保持历史数据最新。
/// </summary>
public class PeriodMaintainer : BackgroundService
{
    private readonly IBarStore _barStore;
    private readonly SessionScheduler _scheduler;
    private readonly BuildPeriodsService _builder;
    private readonly ILogger<PeriodMaintainer> _log;

    public PeriodMaintainer(IBarStore barStore, SessionScheduler scheduler,
                            BuildPeriodsService builder, ILogger<PeriodMaintainer> log)
    {
        _barStore = barStore;
        _scheduler = scheduler;
        _builder = builder;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Console.WriteLine("[PeriodMaintainer] Starting...");

        // Only works with DuckDB
        if (_barStore is not DuckDBStore duckStore)
        {
            Console.WriteLine($"[PeriodMaintainer] Not DuckDB (type={_barStore.GetType().Name}) — skipping");
            _log.LogInformation("PeriodMaintainer: Not DuckDB — skipping");
            return;
        }

        var dbPath = duckStore.DbPath;
        Console.WriteLine($"[PeriodMaintainer] DuckDB mode — DB={dbPath}");
        _log.LogInformation("PeriodMaintainer started, DB={Db}", dbPath);

        var lastBuild = DateTime.MinValue;
        var sessionJustClosed = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var inSession = _scheduler.IsInSession();

                // Build strategy:
                // - During session: every 5 min (incremental, fast)
                // - Just after session close: once (final catch-up)
                // - Outside session: every 30 min idle check (no-op if no new data)
                var shouldBuild = false;
                var interval = TimeSpan.FromMinutes(30); // idle interval

                if (inSession)
                {
                    interval = TimeSpan.FromMinutes(5);
                    if (now - lastBuild >= TimeSpan.FromMinutes(5))
                        shouldBuild = true;
                    sessionJustClosed = false;
                }
                else if (!sessionJustClosed && lastBuild > DateTime.MinValue)
                {
                    // Session just ended — one final build
                    sessionJustClosed = true;
                    shouldBuild = true;
                    _log.LogInformation("Session closed — final multi-period sync");
                }

                if (shouldBuild)
                {
                    await _builder.RunAsync(dbPath, fullRebuild: false, ct);
                    lastBuild = now;
                }

                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "PeriodMaintainer build failed — retry in 60s");
                try { await Task.Delay(60_000, ct); } catch { break; }
            }
        }

        _log.LogInformation("PeriodMaintainer stopped");
    }
}
