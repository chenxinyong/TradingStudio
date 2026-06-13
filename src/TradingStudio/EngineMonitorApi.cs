namespace TradingStudio;

/// <summary>
/// 引擎监控 API — 内嵌于 TradingStudio 进程。
/// REST 查询 + 控制命令。实时推送走 SignalR Hub。
/// </summary>
public static class EngineMonitorApi
{
    public static void MapEndpoints(WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ═══ 快照查询 (GET) ═══
        api.MapGet("/health", () =>
        {
            // Phase 3: 从 HealthMonitor 获取快照
            return Results.Ok(new { Status = "Phase 3" });
        });

        api.MapGet("/portfolio", () =>
        {
            // Phase 3: 从 PortfolioManager 获取快照
            return Results.Ok(new { Phase = 3 });
        });

        api.MapGet("/strategies", () =>
        {
            // Phase 3: 从 StrategyContainer 获取快照
            return Results.Ok(new { Phase = 3 });
        });

        api.MapGet("/strategies/{id}", (string id) =>
        {
            // Phase 3: 单策略快照
            return Results.Ok(new { StrategyId = id, Phase = 3 });
        });

        api.MapGet("/orders", () =>
        {
            // Phase 3: 从 ExecutionHandler 获取活跃订单
            return Results.Ok(new { Phase = 3 });
        });

        api.MapGet("/trades", () =>
        {
            // Phase 3: 从 PortfolioManager 获取今日成交
            return Results.Ok(new { Phase = 3 });
        });

        api.MapGet("/alerts", () =>
        {
            // Phase 3: 从 FeedbackMonitor 获取告警列表
            return Results.Ok(new { Phase = 3 });
        });

        api.MapGet("/indicators/{strategyId}", (string strategyId) =>
        {
            // Phase 3: 从 IndicatorManager 获取指标快照
            return Results.Ok(new { StrategyId = strategyId, Phase = 3 });
        });

        // ═══ 控制命令 (POST) ═══
        api.MapPost("/strategies/{id}/pause", (string id) =>
        {
            // Phase 3: StrategyContainer.Pause(id)
            return Results.Ok(new { StrategyId = id, Action = "pause", Phase = 3 });
        });

        api.MapPost("/strategies/{id}/resume", (string id) =>
        {
            // Phase 3: StrategyContainer.Resume(id)
            return Results.Ok(new { StrategyId = id, Action = "resume", Phase = 3 });
        });

        api.MapPost("/strategies/{id}/tighten", (string id, TightenRiskRequest req) =>
        {
            // Phase 3: StrategyContainer.TightenRisk(id, req.RuleName, req.NewValue)
            return Results.Ok(new { StrategyId = id, Rule = req.RuleName, Phase = 3 });
        });

        api.MapPost("/orders/close-position", (ClosePositionRequest req) =>
        {
            // Phase 3: 紧急平仓
            return Results.Ok(new { InstrumentId = req.InstrumentId, Phase = 3 });
        });

        api.MapPost("/config/reload", () =>
        {
            // Phase 3: 重新加载策略配置
            return Results.Ok(new { Action = "reload", Phase = 3 });
        });
    }
}

/// <summary>风控收紧请求</summary>
public record TightenRiskRequest(string RuleName, string NewValue);

/// <summary>手动平仓请求</summary>
public record ClosePositionRequest(string InstrumentId);
