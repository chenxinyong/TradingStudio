using TradingStudio.Engine;

namespace TradingStudio.Services;

/// <summary>
/// 实盘引擎宿主 — 将 TradingEngine 的 7×24 主循环嵌入 ASP.NET Core 生命周期。
/// WebApplication 启动后，引擎在后台运行，CTP 行情/交易通过 DI 注入。
/// </summary>
public class EngineHost : BackgroundService
{
    private readonly TradingEngine _engine;
    private readonly ILogger<EngineHost> _log;

    public EngineHost(TradingEngine engine, ILogger<EngineHost> log)
    {
        _engine = engine;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Engine starting...");
        try
        {
            await _engine.RunAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Engine fatal error");
            throw;
        }
        _log.LogInformation("Engine stopped");
    }
}
