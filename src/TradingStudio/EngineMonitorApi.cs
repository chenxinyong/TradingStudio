using DuckDB.NET.Data;
using Microsoft.AspNetCore.Mvc;
using TradingStudio.Core.Strategy;
using TradingStudio.Engine;
using TradingStudio.Services;
using TradingStudio.Engine.Statistics;

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

        api.MapGet("/health", ([FromServices] PortfolioManager? portfolio, [FromServices] TickSnapshot? ticks) =>
        {
            return Results.Ok(new
            {
                Status = "Running",
                ConnectedInstruments = ticks?.Count ?? 0,
                TotalEquity = portfolio?.Equity ?? 0,
                LastUpdate = DateTimeOffset.UtcNow,
            });
        });

        api.MapGet("/portfolio", ([FromServices] PortfolioManager portfolio) =>
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

        api.MapGet("/strategies", ([FromServices] StrategyContainer strategies) =>
        {
            return Results.Ok(strategies.GetAllSnapshots());
        });

        api.MapGet("/strategies/{id}", (string id, [FromServices] StrategyContainer strategies) =>
        {
            var s = strategies.GetSnapshot(id);
            return s != null ? Results.Ok(s) : Results.NotFound();
        });

        api.MapGet("/orders", ([FromServices] ExecutionHandler execution) =>
        {
            return Results.Ok(new
            {
                Active = execution.ActiveOrders,
                History = execution.OrderHistory.TakeLast(100),
            });
        });

        api.MapGet("/trades", ([FromServices] PortfolioManager portfolio) =>
        {
            return Results.Ok(portfolio.TradeHistory.TakeLast(100));
        });

        api.MapGet("/alerts", ([FromServices] FeedbackMonitor feedback) =>
        {
            return Results.Ok(feedback.RecentAlerts);
        });

        api.MapGet("/ticks", ([FromServices] TickSnapshot ticks) =>
        {
            return Results.Ok(ticks.GetAll());
        });

        api.MapGet("/ticks/{instrumentId}", (string instrumentId, [FromServices] TickSnapshot ticks) =>
        {
            var t = ticks.Get(instrumentId);
            return t != null ? Results.Ok(t) : Results.NotFound();
        });

        api.MapGet("/indicators/{strategyId}", (string strategyId) =>
        {
            // Phase 3: 从 IndicatorManager 获取指标快照
            return Results.Ok(new { StrategyId = strategyId, Phase = 3 });
        });

        // ═══ 品种列表 ═══
        api.MapGet("/products", () =>
        {
            var products = BarQueryHelper.QueryProducts();
            return Results.Ok(products.Select(p => new {
                p.Code, p.InstrumentId, p.BarCount, p.FirstBar, p.LastBar
            }));
        });

        // ═══ Bar 数据查询 ═══
        api.MapGet("/bars/{instrumentId}", (
            string instrumentId,
            [FromQuery] string? freq,
            [FromQuery] string? table) =>
        {
            var f = freq ?? "15min";
            var tbl = table ?? "bars_5min";
            var bars = BarQueryHelper.QueryBars(instrumentId, f, tbl);
            return bars.Count > 0 ? Results.Ok(bars) : Results.NotFound();
        });

        // ═══ 回测管理 ═══
        api.MapPost("/backtest/start", ([FromBody] StrategyConfig config,
            [FromServices] BacktestRunner runner) =>
        {
            var id = runner.Start(config);
            return Results.Ok(new { TaskId = id, Status = "started" });
        });

        api.MapGet("/backtest/{id}/status", (string id,
            [FromServices] BacktestRunner runner) =>
        {
            var task = runner.Get(id);
            if (task == null) return Results.NotFound();
            return Results.Ok(new { task.Id, task.Status, task.StartedAt, task.CompletedAt, task.Error });
        });

        api.MapGet("/backtest/{id}/report", (string id,
            [FromServices] BacktestRunner runner) =>
        {
            var task = runner.Get(id);
            if (task == null) return Results.NotFound();
            if (task.Status != "completed") return Results.Ok(new { task.Status, Message = "Backtest still running" });
            return Results.Content(task.ReportJson ?? "{}", "application/json");
        });

        api.MapGet("/backtest/{id}/summary", (string id,
            [FromServices] BacktestRunner runner) =>
        {
            var task = runner.Get(id);
            if (task?.Summary == null) return Results.NotFound();
            return Results.Ok(task.Summary);
        });

        api.MapGet("/backtest/history", ([FromServices] BacktestRunner runner) =>
        {
            return Results.Ok(runner.All.Select(t => new { t.Id, t.Status, t.StartedAt, t.CompletedAt,
                t.Summary?.StrategyId, t.Summary?.TotalTrades, t.Summary?.TotalNetProfit }));
        });

        // ═══ 控制命令 (POST) ═══

        api.MapPost("/strategies/{id}/pause", (string id, [FromServices] StrategyContainer strategies) =>
        {
            strategies.Pause(id);
            return Results.Ok(new { StrategyId = id, Action = "pause" });
        });

        api.MapPost("/strategies/{id}/resume", (string id, [FromServices] StrategyContainer strategies) =>
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

/// <summary>API 响应类型</summary>
public record BarDto(DateTime Dt, double Open, double High, double Low, double Close, long Volume);
public record ProductInfo(string Code, string InstrumentId, long BarCount, string FirstBar, string LastBar);

/// <summary>Bar 数据查询辅助方法</summary>
internal static class BarQueryHelper
{
    private const string DbPath = "../../data/bars_history.duckdb";

    public static List<ProductInfo> QueryProducts()
    {
        try
        {
            using var conn = new DuckDBConnection($"Data Source={DbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT REPLACE(instrument_id, '000', '') as code,
                       instrument_id, COUNT(*) as bars,
                       MIN(bar_time)::VARCHAR, MAX(bar_time)::VARCHAR
                FROM bars_5min
                WHERE instrument_id LIKE '%000' AND instrument_id NOT LIKE '%F000'
                GROUP BY instrument_id ORDER BY code
                """;
            using var reader = (DuckDBDataReader)cmd.ExecuteReader();
            var list = new List<ProductInfo>();
            while (reader.Read())
                list.Add(new ProductInfo(reader.GetString(0).ToUpper(), reader.GetString(1),
                    reader.GetInt64(2), reader.GetString(3), reader.GetString(4)));
            return list;
        }
        catch { return new List<ProductInfo>(); }
    }

    public static List<BarDto> QueryBars(string instrumentId, string freq, string table)
    {
        try
        {
            using var conn = new DuckDBConnection($"Data Source={DbPath}");
            conn.Open();

            string sql = freq switch
            {
                "day" => $"""
                    SELECT trading_day::TIMESTAMP as dt,
                        FIRST(open)/1e7, MAX(high)/1e7, MIN(low)/1e7, LAST(close)/1e7, SUM(volume)
                    FROM {table} WHERE instrument_id = '{instrumentId}'
                    GROUP BY trading_day ORDER BY dt
                    """,
                "week" => $"""
                    SELECT trading_week::TIMESTAMP as dt,
                        FIRST(open)/1e7, MAX(high)/1e7, MIN(low)/1e7, LAST(close)/1e7, SUM(volume)
                    FROM {table} WHERE instrument_id = '{instrumentId}'
                    GROUP BY trading_week ORDER BY dt
                    """,
                _ => int.TryParse(freq.Replace("min", ""), out int p) && p > 1
                    ? $"""
                    SELECT date_trunc('hour', bar_time::TIMESTAMP) +
                           INTERVAL (FLOOR(EXTRACT(minute FROM bar_time::TIMESTAMP) / {p}) * {p}) MINUTE as dt,
                        FIRST(open)/1e7, MAX(high)/1e7, MIN(low)/1e7, LAST(close)/1e7, SUM(volume)
                    FROM {table} WHERE instrument_id = '{instrumentId}'
                    GROUP BY dt ORDER BY dt
                    """
                    : $"""
                    SELECT bar_time::TIMESTAMP as dt,
                        open/1e7, high/1e7, low/1e7, close/1e7, volume
                    FROM {table} WHERE instrument_id = '{instrumentId}'
                    ORDER BY dt
                    """
            };

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = (DuckDBDataReader)cmd.ExecuteReader();

            var bars = new List<BarDto>();
            var dtIdx = reader.GetOrdinal("dt");
            while (reader.Read())
            {
                bars.Add(new BarDto(
                    reader.GetDateTime(dtIdx),
                    reader.GetDouble(1), reader.GetDouble(2),
                    reader.GetDouble(3), reader.GetDouble(4),
                    reader.GetInt64(5)));
            }
            return bars;
        }
        catch
        {
            return new List<BarDto>();
        }
    }
}
