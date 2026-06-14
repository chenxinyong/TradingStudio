using TradingStudio.Core.Engine;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine;

/// <summary>
/// 多策略容器。管理策略注册、事件路由、生命周期。
/// </summary>
public class StrategyContainer
{
    private readonly SortedDictionary<string, List<StrategySlot>> _subscriptions = new();
    private readonly List<StrategySlot> _allSlots = new();

    public IReadOnlyList<StrategySlot> AllSlots => _allSlots;

    public void Register(IStrategy strategy, StrategyConfig config, StrategyContext context)
    {
        var slot = new StrategySlot(strategy, config, context);
        _allSlots.Add(slot);
        foreach (var inst in config.Instruments)
        {
            if (!_subscriptions.ContainsKey(inst))
                _subscriptions[inst] = new List<StrategySlot>();
            _subscriptions[inst].Add(slot);
        }
    }

    public void Pause(string strategyId)
    {
        var slot = _allSlots.FirstOrDefault(s => s.Config.StrategyId == strategyId);
        if (slot != null) slot.IsActive = false;
    }

    public bool Resume(string strategyId)
    {
        var slot = _allSlots.FirstOrDefault(s => s.Config.StrategyId == strategyId);
        if (slot != null) slot.IsActive = true;
        return slot != null;
    }

    public bool IsActive(string strategyId) =>
        _allSlots.FirstOrDefault(s => s.Config.StrategyId == strategyId)?.IsActive ?? false;

    public void DispatchTick(TickEvent tickEvt)
    {
        if (!_subscriptions.TryGetValue(tickEvt.InstrumentId, out var slots)) return;
        foreach (var slot in slots)
        {
            if (slot.IsActive)
                slot.Strategy.OnTick(tickEvt.Tick, tickEvt.InstrumentId);
        }
    }

    public void DispatchBar(BarEvent barEvt)
    {
        var inst = barEvt.Bar.InstrumentId;
        if (!_subscriptions.TryGetValue(inst, out var slots)) return;
        foreach (var slot in slots)
        {
            if (slot.IsActive)
            {
                try { slot.Strategy.OnBar(barEvt.Bar); }
                catch (Exception ex) { Console.Error.WriteLine($"[{slot.Config.StrategyId}] OnBar error: {ex.Message}"); }
            }
        }
    }

    public void DispatchOrderEvent(OrderEvent evt)
    {
        var slot = _allSlots.FirstOrDefault(s => s.Config.StrategyId == evt.StrategyId);
        if (slot != null && slot.IsActive)
        {
            try { slot.Strategy.OnOrderEvent(evt); }
            catch (Exception ex) { Console.Error.WriteLine($"[{slot.Config.StrategyId}] OnOrderEvent error: {ex.Message}"); }
        }
    }

    public void DispatchAlert(IReadOnlyList<MonitorAlert> alerts)
    {
        foreach (var alert in alerts)
        {
            var slot = _allSlots.FirstOrDefault(s => s.Config.StrategyId == alert.StrategyId);
            if (slot != null && slot.IsActive)
                slot.Strategy.OnAlert(alert);
        }
    }

    public void DispatchEndOfAlgorithm()
    {
        foreach (var slot in _allSlots)
        {
            if (slot.IsActive)
            {
                try { slot.Strategy.OnEndOfAlgorithm(); }
                catch (Exception ex) { Console.Error.WriteLine($"[{slot.Config.StrategyId}] OnEnd error: {ex.Message}"); }
            }
        }
    }

    public IReadOnlyList<StrategySnapshot> GetAllSnapshots() =>
        _allSlots.Select(s => new StrategySnapshot
        {
            StrategyId = s.Config.StrategyId,
            Status = s.IsActive ? "Running" : "Paused",
        }).ToList();

    public StrategySnapshot? GetSnapshot(string strategyId) =>
        GetAllSnapshots().FirstOrDefault(s => s.StrategyId == strategyId);
}

public class StrategySlot
{
    public IStrategy Strategy { get; }
    public StrategyConfig Config { get; }
    public StrategyContext Context { get; }
    public bool IsActive { get; set; } = true;

    public StrategySlot(IStrategy strategy, StrategyConfig config, StrategyContext context)
    {
        Strategy = strategy;
        Config = config;
        Context = context;
    }
}

public record StrategySnapshot
{
    public string StrategyId { get; init; } = "";
    public string Status { get; init; } = "Running";
}
