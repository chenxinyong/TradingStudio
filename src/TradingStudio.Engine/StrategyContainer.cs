using System.Reflection;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine;

/// <summary>
/// 多策略容器。管理策略注册、事件路由、生命周期、参数热更新。
///
/// 线程安全：Register/Unregister/Pause/Resume/UpdateParameter 使用 _mutex 保护
/// _allSlots 的修改。Dispatch* 方法无需锁（只读遍历，Slot 替换时旧 Slot 不会被修改）。
///
/// StrategyParam&lt;T&gt;.Value 更新是最终一致性：API 线程写入后，下次 DispatchBar/Tick
/// 时自动读取新值（通过 implicit operator T 每次解引用）。
/// </summary>
public class StrategyContainer
{
    private readonly SortedDictionary<string, List<StrategySlot>> _subscriptions = new();
    private readonly List<StrategySlot> _allSlots = new();
    private readonly object _mutex = new();

    public IReadOnlyList<StrategySlot> AllSlots => _allSlots;

    public void Register(IStrategy strategy, StrategyConfig config, StrategyContext context)
    {
        var slot = new StrategySlot(strategy, config, context);
        lock (_mutex) { _allSlots.Add(slot); }
        foreach (var inst in config.Instruments)
        {
            if (!_subscriptions.ContainsKey(inst))
                _subscriptions[inst] = new List<StrategySlot>();
            _subscriptions[inst].Add(slot);
        }
    }

    public void Unregister(string strategyId)
    {
        StrategySlot? slot;
        lock (_mutex)
        {
            slot = _allSlots.FirstOrDefault(s => s.Config.StrategyId == strategyId);
            if (slot == null) return;
            _allSlots.Remove(slot);
        }
        foreach (var inst in slot.Config.Instruments)
            if (_subscriptions.TryGetValue(inst, out var list))
                list.Remove(slot);
    }

    public void Pause(string strategyId)
    {
        lock (_mutex)
        {
            var slot = _allSlots.FirstOrDefault(s => s.Config.StrategyId == strategyId);
            if (slot != null) slot.IsActive = false;
        }
    }

    public bool Resume(string strategyId)
    {
        lock (_mutex)
        {
            var slot = _allSlots.FirstOrDefault(s => s.Config.StrategyId == strategyId);
            if (slot != null) slot.IsActive = true;
            return slot != null;
        }
    }

    public bool IsActive(string strategyId) =>
        _allSlots.FirstOrDefault(s => s.Config.StrategyId == strategyId)?.IsActive ?? false;

    /// <summary>
    /// 热更新策略参数 — 不停机修改 StrategyParam&lt;T&gt;.Value。
    ///
    /// 新值在下次 DispatchBar/DispatchTick 时自动生效（implicit operator T 每次解引用）。
    /// 验证: 如果参数有 Validator 委托，先验证再写入。
    ///
    /// 返回: true=更新成功, false=策略或参数不存在 / 验证失败
    /// </summary>
    public (bool Success, string? Error) UpdateParameter(string strategyId, string paramName, object value)
    {
        StrategySlot? slot;
        lock (_mutex)
        {
            slot = _allSlots.FirstOrDefault(s => s.Config.StrategyId == strategyId);
        }
        if (slot == null)
            return (false, $"Strategy '{strategyId}' not found");

        if (!slot.ParamIndex.TryGetValue(paramName, out var entry))
            return (false, $"Parameter '{paramName}' not found in '{strategyId}'. Available: {string.Join(", ", slot.ParamIndex.Keys.OrderBy(k => k))}");

        try
        {
            var paramType = entry.Instance.GetType();
            var targetType = paramType.GetGenericArguments()[0];
            var converted = Convert.ChangeType(value, targetType);

            // 验证（如果有 Validator）
            var validator = paramType.GetProperty("Validator")?.GetValue(entry.Instance) as Delegate;
            if (validator != null)
            {
                var ok = (bool?)validator.DynamicInvoke(converted) ?? true;
                if (!ok)
                    return (false, $"Validation failed: {paramName}={converted} (validator rejected)");
            }

            // 写入 Value
            var valueProp = paramType.GetProperty("Value");
            valueProp?.SetValue(entry.Instance, converted);

            // 同步更新 Config.Parameters（供 API 查询用）
            // StrategyParameters.Add 即 upsert（内部 _values[name]=value）
            slot.Config.Parameters.Add(paramName, converted);

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Update failed: {ex.Message}");
        }
    }

    /// <summary>批量更新参数（一次性应用多个参数，避免逐条更新的中间状态）</summary>
    public (bool Success, string? Error) UpdateParameters(string strategyId, Dictionary<string, object> paramUpdates)
    {
        var errors = new List<string>();
        foreach (var (name, value) in paramUpdates)
        {
            var (ok, err) = UpdateParameter(strategyId, name, value);
            if (!ok) errors.Add($"{name}: {err}");
        }
        return errors.Count == 0
            ? (true, null)
            : (false, $"{errors.Count}/{paramUpdates.Count} params failed: " + string.Join("; ", errors));
    }

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
            StrategyType = s.Config.StrategyType,
            Status = s.IsActive ? "Running" : "Paused",
            Instruments = s.Config.Instruments.ToList(),
            AllocatedCapital = s.Config.AllocatedCapital,
            CurrentEquity = s.Context.Equity,
            PositionCount = s.Context.Positions.Count,
            ActiveOrderCount = 0, // Phase 3: from ExecutionHandler
            Parameters = s.Config.Parameters
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString() ?? ""),
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

    /// <summary>
    /// 参数索引缓存 — key=参数名, value=(成员信息, StrategyParam&lt;T&gt; 实例)。
    /// 在 Register 时构建，避免每次 UpdateParameter 都反射扫描。
    /// </summary>
    public Dictionary<string, (MemberInfo Member, object Instance)> ParamIndex { get; }

    public StrategySlot(IStrategy strategy, StrategyConfig config, StrategyContext context)
    {
        Strategy = strategy;
        Config = config;
        Context = context;
        ParamIndex = BuildParamIndex(strategy);
    }

    private static Dictionary<string, (MemberInfo Member, object Instance)> BuildParamIndex(IStrategy strategy)
    {
        var index = new Dictionary<string, (MemberInfo, object)>(StringComparer.OrdinalIgnoreCase);

        // 扫属性
        foreach (var prop in strategy.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.PropertyType.IsGenericType) continue;
            if (prop.PropertyType.GetGenericTypeDefinition() != typeof(StrategyParam<>)) continue;
            var instance = prop.GetValue(strategy);
            if (instance == null) continue;
            var name = prop.PropertyType.GetProperty("Name")?.GetValue(instance)?.ToString();
            if (!string.IsNullOrEmpty(name) && !index.ContainsKey(name))
                index[name] = (prop, instance);
        }

        // 扫字段
        foreach (var field in strategy.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!field.FieldType.IsGenericType) continue;
            if (field.FieldType.GetGenericTypeDefinition() != typeof(StrategyParam<>)) continue;
            var instance = field.GetValue(strategy);
            if (instance == null) continue;
            var name = field.FieldType.GetProperty("Name")?.GetValue(instance)?.ToString();
            if (!string.IsNullOrEmpty(name) && !index.ContainsKey(name))
                index[name] = (field, instance);
        }

        return index;
    }
}

public record StrategySnapshot
{
    public string StrategyId { get; init; } = "";
    public string StrategyType { get; init; } = "";
    public string Status { get; init; } = "Running";
    public IReadOnlyList<string> Instruments { get; init; } = [];
    public decimal AllocatedCapital { get; init; }
    public decimal CurrentEquity { get; init; }
    public int PositionCount { get; init; }
    public int ActiveOrderCount { get; init; }
    /// <summary>当前参数快照 (string→string，供 UI 热更新后实时展示)</summary>
    public Dictionary<string, string> Parameters { get; init; } = [];
}
