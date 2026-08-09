using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Risk;

namespace TradingStudio.Engine;

/// <summary>
/// 信号合并器：将多个策略的 TradeSignal 合并为组合层持仓目标。
/// 不同实现对应不同的组合管理哲学。
///
/// 可组合性：ITargetCombiner 可以嵌套。
/// 详见 docs/design/18-trade-signal-portfolio-target-decoupling.md
/// </summary>
public interface ITargetCombiner
{
    /// <summary>
    /// 将一组 TradeSignal 转换为 PortfolioTarget 列表。
    /// </summary>
    /// <param name="signals">所有策略发出的信号</param>
    /// <param name="portfolio">当前组合状态（权益、现金、现有持仓）</param>
    /// <param name="registry">品种注册表（合约乘数、保证金率等）</param>
    /// <returns>目标持仓列表（未提及的品种 = 维持现仓）</returns>
    List<PortfolioTarget> Combine(
        IReadOnlyList<TradeSignal> signals,
        IPortfolioState portfolio,
        FutureRegistry registry);
}
