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
    private readonly object _sync = new();  // 保护 _positions / _subPortfolios / _trades / Cash / MarginUsed / Equity / PeakEquity 并发访问

    // IPortfolioState — 属性读写均加锁（实盘中 FillChannel 线程和事件循环线程并发访问）
    // 注：lock 内对属性的读写因 Monitor 可重入而安全（方法体已持锁时，getter/setter 重入同一锁）
    public decimal Cash { get { lock (_sync) return _cash; } private set { lock (_sync) _cash = value; } }
    public decimal Equity { get { lock (_sync) return _equity; } private set { lock (_sync) _equity = value; } }
    public decimal MarginUsed { get { lock (_sync) return _marginUsed; } private set { lock (_sync) _marginUsed = value; } }
    public decimal StartingCapital { get; }
    public decimal PeakEquity { get { lock (_sync) return _peakEquity; } private set { lock (_sync) _peakEquity = value; } }
    public decimal TodayPnL { get { lock (_sync) return _todayPnL; } private set { lock (_sync) _todayPnL = value; } }
    public decimal TotalPnL => Equity - StartingCapital;
    private decimal _cash, _equity, _marginUsed, _peakEquity, _todayPnL;

    public Position? GetPosition(string instrumentId)
    {
        lock (_sync) return _positions.GetValueOrDefault(instrumentId);
    }
    public IReadOnlyList<Position> AllPositions
    {
        get { lock (_sync) return _positions.Values.ToList(); }
    }
    public IReadOnlyList<Order> ActiveOrders => []; // Phase 2b
    public IReadOnlyList<Trade> TradeHistory
    {
        get { lock (_sync) return _trades.ToList(); }
    }
    public IReadOnlyList<SubPortfolioState> SubPortfolios
    {
        get
        {
            lock (_sync)
                return _subPortfolios.Values.Select(sp => new SubPortfolioState
                {
                    StrategyId = sp.StrategyId,
                    AllocatedCapital = sp.AllocatedCapital,
                    Equity = sp.Equity,
                    PeakEquity = sp.PeakEquity,
                    TodayPnL = sp.TodayPnL,
                }).ToList();
        }
    }

    public PortfolioManager(decimal totalCapital)
    {
        StartingCapital = totalCapital;
        _cash = totalCapital;
        _equity = totalCapital;
        _peakEquity = totalCapital;
    }

    public SubPortfolio GetSubPortfolio(string strategyId) =>
        _subPortfolios.TryGetValue(strategyId, out var sp) ? sp :
        throw new InvalidOperationException($"Strategy not found: {strategyId}");

    public bool TryGetSubPortfolio(string strategyId, out SubPortfolio? sp)
        => _subPortfolios.TryGetValue(strategyId, out sp);

    public void CreateSubPortfolio(string strategyId, decimal allocatedCapital)
    {
        var sub = new SubPortfolio(strategyId, allocatedCapital);
        _subPortfolios[strategyId] = sub;
    }

    /// <summary>按 Bar 收盘价更新持仓未实现盈亏（线程安全）</summary>
    public void UpdateMarketPrice(Bar bar, Future future)
    {
        lock (_sync)
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
    }

    /// <summary>交割月检查：到期前 2 个月强制平仓，模拟真实交易规则（线程安全）</summary>
    public List<OrderEvent> ForceCloseNearDelivery(Bar bar, Future future, FutureRegistry registry)
    {
        var closes = new List<OrderEvent>();
        List<(string instId, Position pos)> snapshot;
        lock (_sync) { snapshot = _positions.Select(kv => (kv.Key, kv.Value)).ToList(); }

        foreach (var (instId, pos) in snapshot)
        {
            if (pos.Quantity == 0) continue;
            var parsed = ContractCodeGenerator.ParseCode(instId);
            var year = parsed.year;
            var month = parsed.month;
            var deliveryDate = new DateTime(year < 100 ? 2000 + year : year, month, 1);
            var monthsToDelivery = (deliveryDate.Year - bar.BarTime.Year) * 12
                + deliveryDate.Month - bar.BarTime.Month;

            if (monthsToDelivery <= 2)
            {
                var closeDir = pos.Quantity > 0 ? OrderDirection.Sell : OrderDirection.Buy;
                var closeQty = Math.Abs(pos.Quantity);
                var fill = new OrderEvent
                {
                    OrderId = -1, // 系统强平
                    InstrumentId = instId,
                    StrategyId = pos.StrategyId,
                    Direction = closeDir,
                    Quantity = closeQty,
                    OrderQty = closeQty,
                    FilledQty = closeQty,
                    Type = OrderEventType.Filled,
                    FillPrice = bar.CloseDouble > 0 ? (decimal)bar.CloseDouble : (decimal)bar.OpenDouble,
                    Fee = 0, // 强平不扣手续费
                    Slippage = 0,
                    Message = $"Delivery forced close ({monthsToDelivery}mo to delivery)",
                    Time = new DateTimeOffset(bar.BarTime, TimeSpan.Zero),
                };
                ProcessFill(fill, registry); // ProcessFill 内部有 lock(_sync)，因可重入安全
                closes.Add(fill);
            }
        }

        return closes;
    }

    /// <summary>
    /// 处理成交。更新持仓/资金/分账，产生 Trade 记录。（线程安全）
    /// 实盘中从 FillChannel 线程和事件循环线程并发调用，lock(_sync) 保护。
    /// </summary>
    public Trade? ProcessFill(OrderEvent fill, FutureRegistry registry)
    {
        lock (_sync)
        {
            return ProcessFillLocked(fill, registry);
        }
    }

    private Trade? ProcessFillLocked(OrderEvent fill, FutureRegistry registry)
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
                CreatedTime = fill.Time,
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

                    // 平今手续费：当天开当天平 → 使用 CloseTodayFeeRate
                    var closeFee = fill.Fee;
                    var isCloseToday = pos.CreatedTime.Date == fill.Time.Date
                        && future.CloseTodayFeeRate > 0
                        && Math.Abs(future.CloseTodayFeeRate - future.FeeRate) > 0.0000001;
                    if (isCloseToday)
                    {
                        var closeContractValue = fill.FillPrice * mult * Math.Abs(pos.Quantity);
                        closeFee = Math.Max(1m, closeContractValue * (decimal)future.CloseTodayFeeRate);
                    }

                    var pnl = (fill.FillPrice - pos.AvgPrice) * Math.Abs(pos.Quantity) * mult
                        * (pos.Quantity > 0 ? 1 : -1);
                    var trade = new Trade
                    {
                        InstrumentId = key,
                        Quantity = Math.Abs(pos.Quantity),
                        EntryPrice = pos.AvgPrice,
                        ExitPrice = fill.FillPrice,
                        PnL = pnl - pos.Commission - closeFee,
                        Fee = pos.Commission + closeFee,
                        Slippage = fill.Slippage,
                        EntryTime = pos.CreatedTime.DateTime,
                        ExitTime = fill.Time.DateTime,
                        StrategyId = fill.StrategyId,
                    };
                    Cash += pnl - closeFee + pos.Margin;
                    MarginUsed -= pos.Margin;
                    _positions.Remove(key);
                    _trades.Add(trade);

                    // 更新权益
                    Equity = Cash + MarginUsed + _positions.Values.Sum(p => (decimal)p.UnrealizedPnl);
                    if (Equity > PeakEquity) PeakEquity = Equity;

                    // 更新分账
                    if (_subPortfolios.TryGetValue(fill.StrategyId, out var sub))
                    {
                        sub.Cash += pnl - closeFee + pos.Margin;
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

                // 平今手续费检测
                var exitFee = fill.Fee;
                var isCloseToday = pos.CreatedTime.Date == fill.Time.Date
                    && future.CloseTodayFeeRate > 0
                    && Math.Abs(future.CloseTodayFeeRate - future.FeeRate) > 0.0000001;
                if (isCloseToday)
                {
                    var closeContractValue = fill.FillPrice * mult * closeQty;
                    exitFee = Math.Max(1m, closeContractValue * (decimal)future.CloseTodayFeeRate);
                }

                var pnl = (fill.FillPrice - pos.AvgPrice) * closeQty * mult
                    * (pos.Quantity > 0 ? 1 : -1);
                var closeFee = pos.Commission + exitFee;

                // 平仓记录
                var trade = new Trade
                {
                    InstrumentId = key,
                    Quantity = closeQty,
                    EntryPrice = pos.AvgPrice,
                    ExitPrice = fill.FillPrice,
                    PnL = pnl - closeFee,
                    Fee = closeFee,
                    Slippage = fill.Slippage,
                    EntryTime = pos.CreatedTime.DateTime,
                    ExitTime = fill.Time.DateTime,
                    StrategyId = fill.StrategyId,
                };
                Cash += pnl - exitFee + pos.Margin;
                MarginUsed -= pos.Margin;
                _trades.Add(trade);

                // 开新仓
                var remainingQty = fill.Quantity - closeQty;
                var newMargin = future.TradingUnit * fill.FillPrice * remainingQty * marginRate;
                // 新仓开仓费使用标准费率（不是平今费率）
                var openFee = Math.Max(1m, future.TradingUnit * fill.FillPrice * remainingQty * (decimal)future.FeeRate);
                pos = new Position
                {
                    InstrumentId = key,
                    Quantity = (fill.Direction == OrderDirection.Buy ? 1 : -1) * remainingQty,
                    AvgPrice = fill.FillPrice,
                    Commission = openFee,
                    Margin = newMargin,
                    CreatedTime = fill.Time,
                    StrategyId = fill.StrategyId,
                };
                _positions[key] = pos;
                MarginUsed += newMargin;

                if (_subPortfolios.TryGetValue(fill.StrategyId, out var sub))
                {
                    sub.Cash += pnl - exitFee - openFee + pos.Margin - newMargin;
                    sub.MarginUsed = sub.MarginUsed - pos.Margin + newMargin;
                }

                return trade;
            }

            // 加仓：更新 Margin
            pos.Margin = contractValue * marginRate;
            MarginUsed = _positions.Values.Sum(p => p.Margin);
            Cash -= fill.Fee; // 只扣手续费，本金已通过保证金占用
        }

        if (_subPortfolios.TryGetValue(fill.StrategyId, out var sp))
        {
            sp.Cash -= fill.Fee;
            sp.MarginUsed = MarginUsed;
            sp.Positions = _positions.Values
                .Where(p => p.StrategyId == fill.StrategyId)
                .ToList()
                .AsReadOnly();
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
