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
        // Only works with DuckDB
        if (_barStore is not DuckDBStore duckStore)
        {
            _log.LogInformation("PeriodMaintainer: Not DuckDB (type={Type}) — skipping", _barStore.GetType().Name);
            return;
        }

        var dbPath = duckStore.DbPath;
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
                    // 耗时日志 + 超时保护（2026-07-15 曾疑似 build 卡 3h38m，实为日志 sink 冻结——
                    // 保留此保险丝以防真发生 DB 慢查询；DuckDB.NET 的取消是尽力而为，耗时日志保证可观测）
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var buildCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    buildCts.CancelAfter(TimeSpan.FromMinutes(10));
                    try
                    {
                        await _builder.RunAsync(dbPath, fullRebuild: false, buildCts.Token);
                        _log.LogInformation("Multi-period build done in {Sec:F1}s", sw.Elapsed.TotalSeconds);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _log.LogWarning("Multi-period build 超时（{Sec:F0}s），跳过本轮", sw.Elapsed.TotalSeconds);
                    }
                    lastBuild = now;   // 超时也推进，避免每 5min 重复重活
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
