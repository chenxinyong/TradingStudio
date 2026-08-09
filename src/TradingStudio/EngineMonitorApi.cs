using System.Text;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.Mvc;
using TradingStudio.Core.Engine;
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

        api.MapGet("/portfolio", ([FromServices] PortfolioManager portfolio,
            [FromServices] HealthMonitor? health) =>
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
                DailyPnL = health?.DailyPnL ?? 0,
                EquityCurve = health?.EquityCurve.Select(p => new { t = p.Time.ToString("HH:mm:ss"), e = p.Equity }).ToList() ?? [],
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

        // ═══ 策略参数热更新 (不停机) ═══
        api.MapPost("/strategies/{id}/params", (string id, [FromBody] UpdateParameterRequest req,
            [FromServices] StrategyContainer strategies) =>
        {
            var (ok, error) = strategies.UpdateParameter(id, req.ParamName, req.Value);
            if (!ok)
                return Results.BadRequest(new { StrategyId = id, ParamName = req.ParamName, Error = error });
            return Results.Ok(new { StrategyId = id, ParamName = req.ParamName, Value = req.Value });
        });

        // 批量参数更新
        api.MapPost("/strategies/{id}/params/batch", (string id, [FromBody] Dictionary<string, object> updates,
            [FromServices] StrategyContainer strategies) =>
        {
            var (ok, error) = strategies.UpdateParameters(id, updates);
            if (!ok)
                return Results.BadRequest(new { StrategyId = id, Updated = updates.Count, Error = error });
            return Results.Ok(new { StrategyId = id, Updated = updates.Count });
        });

        // ═══ 测试: 手动下单到 Simnow ═══
        api.MapPost("/orders/test", ([FromBody] TestOrderRequest req,
            [FromServices] ExecutionHandler execution) =>
        {
            if (execution.SendToExchange == null)
                return Results.BadRequest("SendToExchange not bound — Trader bridge not connected");

            var order = new Order
            {
                InstrumentId = req.InstrumentId,
                Direction = req.Side == "buy" ? OrderDirection.Buy : OrderDirection.Sell,
                Type = OrderType.Market,
                Quantity = req.Quantity,
                StrategyId = "manual-test",
            };
            var ticket = execution.Submit(order, "manual-test");
            return Results.Ok(new { ticket.OrderId, ticket.Status, order.InstrumentId, order.Direction, order.Quantity });
        });

        api.MapPost("/orders/close-position", (ClosePositionRequest req) =>
        {
            // Phase 3: 紧急平仓
            return Results.Ok(new { req.InstrumentId, Phase = 3 });
        });

        // ═══ 策略热切换 ═══
        api.MapPost("/strategy/reload", async ([FromBody] StrategyConfig config,
            [FromServices] StrategyContainer strategies,
            [FromServices] ExecutionHandler execution,
            [FromServices] PortfolioManager portfolio,
            [FromServices] IServiceProvider sp) =>
        {
            try
            {
                // 1. 平掉所有现有持仓
                foreach (var pos in portfolio.AllPositions.ToList())
                {
                    if (pos.Quantity != 0)
                        execution.Submit(new Order
                        {
                            InstrumentId = pos.InstrumentId,
                            Direction = pos.Quantity > 0 ? OrderDirection.Sell : OrderDirection.Buy,
                            Type = OrderType.Market,
                            Quantity = Math.Abs(pos.Quantity),
                            IsCloseOrder = true,
                            Tag = "热切换平仓",
                        }, config.StrategyId, portfolio);
                }

                // 2. 注销旧策略, 注册新策略
                var oldIds = strategies.GetAllSnapshots().Select(s => s.StrategyId).ToList();
                foreach (var id in oldIds)
                    strategies.Unregister(id);

                // 3. 创建并注册新策略
                var strategy = StrategyFactory.Create(config);
                strategies.Register(strategy, config, null!); // Context will be re-bound by engine

                return Results.Ok(new { Action = "reload", StrategyId = config.StrategyId,
                    ClosedPositions = portfolio.TradeHistory.Count });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Error = ex.Message });
            }
        });

        // ═══ Dashboard ═══
        app.MapGet("/dashboard", () => Results.Content(DashboardHtml, "text/html; charset=utf-8"));
    }

    private static readonly string DashboardHtml = BuildDashboardHtml();

    private static string BuildDashboardHtml()
{
    var sb = new StringBuilder();
    sb.Append("<!DOCTYPE html><html lang=zh><head><meta charset=UTF-8><title>TradingStudio</title>");
    sb.Append("<style>*{margin:0;padding:0;box-sizing:border-box}body{font-family:monospace;background:#1a1a2e;color:#e0e0e0;padding:20px}");
    sb.Append("h1{color:#00d4ff}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:16px}");
    sb.Append(".card{background:#16213e;border-radius:8px;padding:16px;border:1px solid #0f3460}");
    sb.Append(".card h3{color:#00d4ff;font-size:14px}.stat{font-size:24px;font-weight:bold;color:#fff}");
    sb.Append(".green{color:#00ff88}.red{color:#ff4444}table{width:100%;border-collapse:collapse;font-size:12px}");
    sb.Append("th{text-align:left;color:#888;padding:4px 8px}td{padding:4px 8px;border-top:1px solid #0f3460}");
    sb.Append(".refresh{position:fixed;top:16px;right:16px;background:#00d4ff;border:none;padding:8px 16px;border-radius:4px;cursor:pointer}");
    sb.Append("</style></head><body><h1>TradingStudio Live</h1><button class=refresh onclick=load()>Refresh</button><div class=grid>");
    sb.Append("<div class=card><h3>Portfolio</h3><div id=p>Loading...</div></div>");
    sb.Append("<div class=card><h3>Strategies</h3><div id=s>Loading...</div></div>");
    sb.Append("<div class=card><h3>Recent Trades</h3><div id=t>Loading...</div></div>");
    sb.Append("<div class=card><h3>Orders</h3><div id=o>Loading...</div></div></div><script>");
    sb.Append("async function load(){try{");
    sb.Append("let p=await fetch('/api/portfolio').then(r=>r.json());");
    sb.Append("document.getElementById('p').innerHTML='<div class=stat>'+(p.totalEquity||0).toLocaleString()+' CNY</div>';");
    sb.Append("let s=await fetch('/api/strategies').then(r=>r.json());");
    sb.Append("document.getElementById('s').innerHTML=(s||[]).map(x=>'<div>'+x.strategyId+': '+(x.status||'active')+'</div>').join('');");
    sb.Append("let t=await fetch('/api/trades').then(r=>r.json());");
    sb.Append("document.getElementById('t').innerHTML='<table><tr><th>Inst</th><th>PnL</th></tr>'+");
    sb.Append("(t||[]).slice(-10).reverse().map(x=>'<tr><td>'+x.instrumentId+'</td><td class='+(x.pnL>0?'green':'red')+'>'+(x.pnL||0).toFixed(0)+'</td></tr>').join('')+'</table>';");
    sb.Append("let o=await fetch('/api/orders').then(r=>r.json());");
    sb.Append("document.getElementById('o').innerHTML='<table><tr><th>ID</th><th>Inst</th></tr>'+");
    sb.Append("(o?.active||[]).slice(0,10).map(x=>'<tr><td>'+x.orderId+'</td><td>'+x.instrumentId+'</td></tr>').join('')+'</table>';");
    sb.Append("}catch(e){console.error(e)}}load();setInterval(load,5000);");
    sb.Append("</script></body></html>");
    return sb.ToString();
}
}

public record TightenRiskRequest(string RuleName, string NewValue);
public record ClosePositionRequest(string InstrumentId);
public record TestOrderRequest(string InstrumentId, string Side, int Quantity);
public record UpdateParameterRequest(string ParamName, object Value);

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
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"QueryProducts failed: {ex.Message}"); return new List<ProductInfo>(); }
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
