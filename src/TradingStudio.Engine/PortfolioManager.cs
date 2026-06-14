using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Risk;

namespace TradingStudio.Engine;

/// <summary>
/// 仓位与资金管理 — 支持多策略分账。
/// 实现 IPortfolioState 供风控规则只读查询。
/// </summary>
public class PortfolioManager : IPortfolioState
{
    private readonly Dictionary<string, Position> _positions = new();      // key = instId
    private readonly SortedDictionary<string, SubPortfolio> _subPortfolios = new();
    private readonly List<Trade> _trades = new();

    // IPortfolioState
    public decimal Cash { get; private set; }
    public decimal Equity { get; private set; }
    public decimal MarginUsed { get; private set; }
    public decimal StartingCapital { get; }
    public decimal PeakEquity { get; private set; }
    public decimal TodayPnL { get; private set; }
    public decimal TotalPnL => Equity - StartingCapital;
    public Position? GetPosition(string instrumentId) =>
        _positions.GetValueOrDefault(instrumentId);
    public IReadOnlyList<Position> AllPositions => _positions.Values.ToList();
    public IReadOnlyList<Order> ActiveOrders => []; // Phase 2b
    public IReadOnlyList<Trade> TradeHistory => _trades;
    public IReadOnlyList<SubPortfolioState> SubPortfolios =>
        _subPortfolios.Values.Select(sp => new SubPortfolioState
        {
            StrategyId = sp.StrategyId,
            AllocatedCapital = sp.AllocatedCapital,
            Equity = sp.Equity,
            PeakEquity = sp.PeakEquity,
            TodayPnL = sp.TodayPnL,
        }).ToList();

    public PortfolioManager(decimal totalCapital)
    {
        StartingCapital = totalCapital;
        Cash = totalCapital;
        Equity = totalCapital;
        PeakEquity = totalCapital;
    }

    public SubPortfolio GetSubPortfolio(string strategyId) =>
        _subPortfolios.TryGetValue(strategyId, out var sp) ? sp :
        throw new InvalidOperationException($"Strategy not found: {strategyId}");

    public void CreateSubPortfolio(string strategyId, decimal allocatedCapital)
    {
        var sub = new SubPortfolio(strategyId, allocatedCapital);
        _subPortfolios[strategyId] = sub;
    }

    /// <summary>按 Bar 收盘价更新持仓未实现盈亏</summary>
    public void UpdateMarketPrice(Bar bar, Future future)
    {
        var price = (decimal)bar.CloseDouble;
        if (!_positions.TryGetValue(bar.InstrumentId, out var pos)) return;
        var mult = future.TradingUnit;
        pos.MarketPrice = (double)price;
        if (pos.Quantity > 0)
            pos.UnrealizedPnl = (double)((price - pos.AvgPrice) * pos.Quantity * mult);
        else if (pos.Quantity < 0)
            pos.UnrealizedPnl = (double)((pos.AvgPrice - price) * Math.Abs(pos.Quantity) * mult);
        else
            pos.UnrealizedPnl = 0;
        _positions[bar.InstrumentId] = pos;
        Equity = Cash + MarginUsed + _positions.Values.Sum(p => (decimal)p.UnrealizedPnl);
    }

    /// <summary>
    /// 处理成交。更新持仓/资金/分账，产生 Trade 记录。
    /// 多仓：做多 × 做空分开。简化处理：同品种同方向合并。
    /// </summary>
    public Trade? ProcessFill(OrderEvent fill, FutureRegistry registry)
    {
        var future = registry.Resolve(fill.InstrumentId);
        if (future == null) return null;

        var key = fill.InstrumentId;
        var hasPosition = _positions.TryGetValue(key, out var pos);

        // 保证金 = 价格 × 交易单位 × 手数 × 保证金率
        var marginRate = future.MarginRate > 0 ? future.MarginRate : 0.08m;
        var contractValue = fill.FillPrice * future.TradingUnit * fill.Quantity;
        var margin = contractValue * marginRate;

        if (!hasPosition)
        {
            // 开仓
            pos = new Position
            {
                InstrumentId = key,
                Quantity = fill.Direction == OrderDirection.Buy ? fill.Quantity : -fill.Quantity,
                AvgPrice = fill.FillPrice,
                Commission = fill.Fee,
                Margin = margin,
                CreatedTime = DateTimeOffset.UtcNow,
                StrategyId = fill.StrategyId,
            };
            _positions[key] = pos;
            Cash -= fill.Fee;
            MarginUsed += margin;
        }
        else
        {
            var newQty = pos.Quantity + (fill.Direction == OrderDirection.Buy ? fill.Quantity : -fill.Quantity);

            if (Math.Sign(newQty) == Math.Sign(pos.Quantity) || newQty == 0)
            {
                // 加仓或平仓
                if (newQty != 0)
                {
                    // 加仓：加权平均价
                    var totalQty = Math.Abs(pos.Quantity) + fill.Quantity;
                    pos.AvgPrice = (pos.AvgPrice * Math.Abs(pos.Quantity) + fill.FillPrice * fill.Quantity)
                        / totalQty;
                    pos.Quantity = newQty;
                    pos.Commission += fill.Fee;
                    pos.Margin = contractValue * marginRate;
                }
                else
                {
                    // 完全平仓
                    var mult = future.TradingUnit;
                    var pnl = (fill.FillPrice - pos.AvgPrice) * Math.Abs(pos.Quantity) * mult
                        * (pos.Quantity > 0 ? 1 : -1);
                    var trade = new Trade
                    {
                        InstrumentId = key,
                        Quantity = Math.Abs(pos.Quantity),
                        EntryPrice = pos.AvgPrice,
                        ExitPrice = fill.FillPrice,
                        PnL = pnl - pos.Commission - fill.Fee,
                        Fee = pos.Commission + fill.Fee,
                        Slippage = fill.Slippage,
                        EntryTime = pos.CreatedTime.DateTime,
                        ExitTime = DateTime.UtcNow,
                        StrategyId = fill.StrategyId,
                    };
                    Cash += pnl - fill.Fee + pos.Margin;
                    MarginUsed -= pos.Margin;
                    _positions.Remove(key);
                    _trades.Add(trade);

                    // 更新权益
                    Equity = Cash + MarginUsed + _positions.Values.Sum(p => (decimal)p.UnrealizedPnl);
                    if (Equity > PeakEquity) PeakEquity = Equity;

                    // 更新分账
                    if (_subPortfolios.TryGetValue(fill.StrategyId, out var sub))
                    {
                        sub.Cash += pnl - fill.Fee + pos.Margin;
                        sub.MarginUsed -= pos.Margin;
                        if (sub.Equity > sub.PeakEquity) sub.PeakEquity = sub.Equity;
                    }

                    return trade;
                }
            }
            else
            {
                // 反向开仓（先平后开）
                // 简化：先平旧仓，再开新仓
                var closeQty = Math.Abs(pos.Quantity);
                var mult = future.TradingUnit;
                var pnl = (fill.FillPrice - pos.AvgPrice) * closeQty * mult
                    * (pos.Quantity > 0 ? 1 : -1);
                var closeFee = pos.Commission;

                // 平仓记录
                var trade = new Trade
                {
                    InstrumentId = key,
                    Quantity = closeQty,
                    EntryPrice = pos.AvgPrice,
                    ExitPrice = fill.FillPrice,
                    PnL = pnl - closeFee - fill.Fee,
                    Fee = closeFee + fill.Fee,
                    Slippage = fill.Slippage,
                    EntryTime = pos.CreatedTime.DateTime,
                    ExitTime = DateTime.UtcNow,
                    StrategyId = fill.StrategyId,
                };
                Cash += pnl - fill.Fee + pos.Margin;
                MarginUsed -= pos.Margin;
                _trades.Add(trade);

                // 开新仓
                var remainingQty = fill.Quantity - closeQty;
                var newMargin = future.TradingUnit * fill.FillPrice * remainingQty * marginRate;
                pos = new Position
                {
                    InstrumentId = key,
                    Quantity = (fill.Direction == OrderDirection.Buy ? 1 : -1) * remainingQty,
                    AvgPrice = fill.FillPrice,
                    Commission = fill.Fee,
                    Margin = newMargin,
                    CreatedTime = DateTimeOffset.UtcNow,
                    StrategyId = fill.StrategyId,
                };
                _positions[key] = pos;
                MarginUsed += newMargin;

                if (_subPortfolios.TryGetValue(fill.StrategyId, out var sub))
                {
                    sub.Cash += pnl - fill.Fee + pos.Margin - newMargin;
                    sub.MarginUsed = sub.MarginUsed - pos.Margin + newMargin;
                }

                return trade;
            }

            // 加仓：更新 Margin
            pos.Margin = contractValue * marginRate;
            MarginUsed = _positions.Values.Sum(p => p.Margin);
            Cash -= fill.Fee; // weng: 只扣手续费，本金已通过保证金占用
        }

        if (_subPortfolios.TryGetValue(fill.StrategyId, out var sp))
        {
            sp.Cash -= fill.Fee;
            sp.MarginUsed = MarginUsed;
        }

        Equity = Cash + MarginUsed + _positions.Values.Sum(p => (decimal)p.UnrealizedPnl);
        if (Equity > PeakEquity) PeakEquity = Equity;

        return null; // 加仓不产生 Trade
    }
}

/// <summary>策略分账</summary>
public class SubPortfolio
{
    public string StrategyId { get; }
    public decimal AllocatedCapital { get; }
    public decimal Cash { get; internal set; }
    public decimal MarginUsed { get; internal set; }
    public decimal PeakEquity { get; internal set; }
    public decimal TodayPnL { get; internal set; }
    public decimal Equity => Cash + MarginUsed + Positions.Sum(p => (decimal)p.UnrealizedPnl);
    public IReadOnlyList<Position> Positions { get; internal set; } = [];

    public SubPortfolio(string strategyId, decimal allocatedCapital)
    {
        StrategyId = strategyId;
        AllocatedCapital = allocatedCapital;
        Cash = allocatedCapital;
        PeakEquity = allocatedCapital;
    }
}
