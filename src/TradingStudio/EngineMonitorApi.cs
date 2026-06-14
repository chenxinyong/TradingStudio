using TradingStudio.Engine;

namespace TradingStudio;

/// <summary>
/// 引擎监控 API — 对接实盘引擎组件，供 WPF 客户端查询。
/// </summary>
public static class EngineMonitorApi
{
    public static void MapEndpoints(WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ═══ 快照查询 (GET) ═══

        api.MapGet("/health", (PortfolioManager? portfolio, TickSnapshot? ticks) =>
        {
            return Results.Ok(new
            {
                Status = "Running",
                ConnectedInstruments = ticks?.Count ?? 0,
                TotalEquity = portfolio?.Equity ?? 0,
                LastUpdate = DateTimeOffset.UtcNow,
            });
        });

        api.MapGet("/portfolio", (PortfolioManager portfolio) =>
        {
            return Results.Ok(new
            {
                StartingCapital = portfolio.StartingCapital,
                TotalEquity = portfolio.Equity,
                Cash = portfolio.Cash,
                MarginUsed = portfolio.MarginUsed,
                TotalPnL = portfolio.TotalPnL,
                Positions = portfolio.AllPositions,
                TradeCount = portfolio.TradeHistory.Count,
            });
        });

        api.MapGet("/strategies", (StrategyContainer strategies) =>
        {
            return Results.Ok(strategies.GetAllSnapshots());
        });

        api.MapGet("/strategies/{id}", (string id, StrategyContainer strategies) =>
        {
            var s = strategies.GetSnapshot(id);
            return s != null ? Results.Ok(s) : Results.NotFound();
        });

        api.MapGet("/orders", (ExecutionHandler execution) =>
        {
            return Results.Ok(new
            {
                Active = execution.ActiveOrders,
                History = execution.OrderHistory.TakeLast(100),
            });
        });

        api.MapGet("/trades", (PortfolioManager portfolio) =>
        {
            return Results.Ok(portfolio.TradeHistory.TakeLast(100));
        });

        api.MapGet("/alerts", (FeedbackMonitor feedback) =>
        {
            return Results.Ok(feedback.RecentAlerts);
        });

        api.MapGet("/ticks", (TickSnapshot ticks) =>
        {
            return Results.Ok(ticks.GetAll());
        });

        api.MapGet("/ticks/{instrumentId}", (string instrumentId, TickSnapshot ticks) =>
        {
            var t = ticks.Get(instrumentId);
            return t != null ? Results.Ok(t) : Results.NotFound();
        });

        api.MapGet("/indicators/{strategyId}", (string strategyId) =>
        {
            // Phase 3: 从 IndicatorManager 获取指标快照
            return Results.Ok(new { StrategyId = strategyId, Phase = 3 });
        });

        // ═══ 控制命令 (POST) ═══

        api.MapPost("/strategies/{id}/pause", (string id, StrategyContainer strategies) =>
        {
            strategies.Pause(id);
            return Results.Ok(new { StrategyId = id, Action = "pause" });
        });

        api.MapPost("/strategies/{id}/resume", (string id, StrategyContainer strategies) =>
        {
            strategies.Resume(id);
            return Results.Ok(new { StrategyId = id, Action = "resume" });
        });

        api.MapPost("/strategies/{id}/tighten", (string id, TightenRiskRequest req) =>
        {
            // Phase 3: RiskController.Tighten
            return Results.Ok(new { StrategyId = id, Rule = req.RuleName, Phase = 3 });
        });

        api.MapPost("/orders/close-position", (ClosePositionRequest req) =>
        {
            // Phase 3: 紧急平仓
            return Results.Ok(new { req.InstrumentId, Phase = 3 });
        });

        api.MapPost("/config/reload", () =>
        {
            // Phase 3: 重新加载策略配置
            return Results.Ok(new { Action = "reload", Phase = 3 });
        });
    }
}

public record TightenRiskRequest(string RuleName, string NewValue);
public record ClosePositionRequest(string InstrumentId);
