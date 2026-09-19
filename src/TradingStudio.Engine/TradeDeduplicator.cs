using TradingStudio.Core.Engine;

namespace TradingStudio.Engine;

/// <summary>
/// 成交幂等去重：CTP 可能对同一笔成交重复回调 OnRtnTrade（或消费端重放）。
/// 以 (TradeDate, ExchangeId, TradeId) 为幂等键，同一键只放行一次。
/// 回测撮合事件无 TradeId（空），一律不去重，保持回测行为不变。
/// </summary>
public sealed class TradeDeduplicator
{
    private readonly HashSet<string> _seen = new();

    /// <summary>返回 true = 该事件是重复成交，应跳过（不重复计入持仓/资金）。</summary>
    public bool IsDuplicate(OrderEvent fill)
    {
        // 仅真实成交事件参与去重；Submitted/Cancelled/Rejected 不产生持仓变更。
        if (fill.Type is not OrderEventType.Filled and not OrderEventType.PartiallyFilled)
            return false;
        // 无 TradeId（回测撮合/系统强平）不去重。
        if (string.IsNullOrEmpty(fill.TradeId))
            return false;

        var key = $"{fill.TradeDate}|{fill.ExchangeId}|{fill.TradeId}";
        return !_seen.Add(key);
    }
}
