using System.Reflection;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine;

/// <summary>
/// 策略工厂 — 显式注册表 + 参数注入。
/// 支持两种参数模式:
///   1. 新 (推荐): StrategyParam&lt;T&gt; 强类型参数字段 (借鉴 StockSharp)
///   2. 旧 (兼容): [StrategyParameter] 特性标注的属性
/// </summary>
public static class StrategyFactory
{
    private static readonly Dictionary<string, Type> _registry = new();

    public static void Register<T>(string name) where T : IStrategy
        => _registry[name] = typeof(T);

    /// <summary>自动发现程序集中所有 IStrategy 实现并注册（类名去掉 Strategy 后缀）</summary>
    public static void DiscoverFromAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type is { IsAbstract: false, IsInterface: false } && typeof(IStrategy).IsAssignableFrom(type))
            {
                var name = type.Name;
                if (name.EndsWith("Strategy")) name = name[..^8];
                if (!_registry.ContainsKey(name)) _registry[name] = type;
            }
        }
    }

    public static IReadOnlyList<string> RegisteredNames => _registry.Keys.OrderBy(k => k).ToList();

    public static IStrategy Create(StrategyConfig config)
    {
        if (!_registry.TryGetValue(config.StrategyType, out var type))
            throw new InvalidOperationException(
                $"未注册的策略: {config.StrategyType}。请先调用 Register<T>(\"{config.StrategyType}\")");

        var strategy = (IStrategy)Activator.CreateInstance(type)!;

        // 检测参数模式：StrategyParam<T> 字段优先，回退到 [StrategyParameter] 属性
        var paramFields = ScanParamFields(strategy);
        var paramProps = ScanParamProps(type);

        foreach (var (key, value) in config.Parameters)
        {
            if (paramFields.TryGetValue(key, out var entry))
                ApplyParamField(strategy, entry, value);
            else if (paramProps.TryGetValue(key, out var pi))
                pi.SetValue(strategy, Convert.ChangeType(value, pi.PropertyType));
            else
                Console.Error.WriteLine(
                    $"[StrategyFactory] 警告: {config.StrategyType} 无参数 '{key}'，已忽略。" +
                    $"可用: {string.Join(", ", paramFields.Keys.Union(paramProps.Keys).OrderBy(k => k))}");
        }

        // 验证新旧两种参数
        ValidateParamFields(strategy, paramFields);
        ValidateParamProps(strategy, paramProps);

        return strategy;
    }

    // ─── StrategyParam<T> 字段扫描 ───

    // StrategyParam<T> 支持字段 + 属性, 用统一的 MemberInfo 处理
    private static Dictionary<string, (MemberInfo Member, object Instance)> ScanParamFields(IStrategy strategy)
    {
        var result = new Dictionary<string, (MemberInfo, object)>(StringComparer.OrdinalIgnoreCase);

        // 扫属性 (get-only auto-property)
        foreach (var prop in strategy.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.PropertyType.IsGenericType) continue;
            if (prop.PropertyType.GetGenericTypeDefinition() != typeof(StrategyParam<>)) continue;
            var instance = prop.GetValue(strategy);
            if (instance == null) continue;
            var name = prop.PropertyType.GetProperty("Name")?.GetValue(instance)?.ToString();
            if (!string.IsNullOrEmpty(name)) result[name] = (prop, instance);
        }

        // 扫字段
        foreach (var field in strategy.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!field.FieldType.IsGenericType) continue;
            if (field.FieldType.GetGenericTypeDefinition() != typeof(StrategyParam<>)) continue;
            var instance = field.GetValue(strategy);
            if (instance == null) continue;
            var name = field.FieldType.GetProperty("Name")?.GetValue(instance)?.ToString();
            if (!string.IsNullOrEmpty(name) && !result.ContainsKey(name)) result[name] = (field, instance);
        }
        return result;
    }

    private static void ApplyParamField(IStrategy strategy, (MemberInfo Member, object Instance) entry, object value)
    {
        var (_, instance) = entry;
        var paramType = instance.GetType();
        var valueProp = paramType.GetProperty("Value");
        var targetType = paramType.GetGenericArguments()[0];
        valueProp?.SetValue(instance, Convert.ChangeType(value, targetType));
    }

    private static void ValidateParamFields(IStrategy strategy,
        Dictionary<string, (MemberInfo Member, object Instance)> fields)
    {
        foreach (var (name, (_, instance)) in fields)
        {
            var paramType = instance.GetType();
            var validator = paramType.GetProperty("Validator")?.GetValue(instance) as Delegate;
            if (validator == null) continue;

            var current = paramType.GetProperty("Value")?.GetValue(instance);
            var ok = (bool?)validator.DynamicInvoke(current) ?? true;
            if (!ok)
                throw new InvalidOperationException(
                    $"{strategy.GetType().Name}: 参数 '{name}'={current} 验证失败");
        }
    }

    /// <summary>
    /// 提取可优化参数列表 — 从 StrategyParam&lt;T&gt; 字段中读取 OptimizeRange。
    /// 供 WalkForwardCommand / 参数扫描工具使用。
    /// </summary>
    public static List<ParamMeta> GetOptimizableParams(IStrategy strategy)
    {
        var result = new List<ParamMeta>();
        foreach (var (_, (_, instance)) in ScanParamFields(strategy))
        {
            var paramType = instance.GetType();
            var range = paramType.GetProperty("OptimizeRange")?.GetValue(instance);
            if (range == null) continue;

            var name = paramType.GetProperty("Name")?.GetValue(instance)?.ToString() ?? "";
            var group = paramType.GetProperty("Group")?.GetValue(instance)?.ToString() ?? "General";

            var rt = range.GetType();
            // ValueTuple 的 Item1/Item2/Item3 是字段(Field)，不是属性(Property)
            var min = rt.GetField("Item1")?.GetValue(range);
            var max = rt.GetField("Item2")?.GetValue(range);
            var step = rt.GetField("Item3")?.GetValue(range);

            if (min != null && max != null)
                result.Add(new ParamMeta
                {
                    Name = name, Group = group,
                    Min = Convert.ToDouble(min), Max = Convert.ToDouble(max),
                    Step = step != null ? Convert.ToDouble(step) : 0,
                    ValueType = paramType.GetGenericArguments()[0].Name,
                });
        }
        return result;
    }

    // ─── [StrategyParameter] 属性扫描（兼容旧策略）───

    private static Dictionary<string, PropertyInfo> ScanParamProps(Type type)
    {
        var result = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in type.GetProperties())
        {
            if (prop.GetCustomAttribute<StrategyParameterAttribute>() is { } attr)
                result[prop.Name] = prop;
        }
        return result;
    }

    private static void ValidateParamProps(IStrategy strategy, Dictionary<string, PropertyInfo> props)
    {
        foreach (var (name, prop) in props)
        {
            var attr = prop.GetCustomAttribute<StrategyParameterAttribute>()!;
            var value = prop.GetValue(strategy);
            if (attr.Required && value == null)
                throw new InvalidOperationException($"{strategy.GetType().Name}: 缺少必需参数 '{name}'");
            if (value is double d && (d < attr.Min || d > attr.Max))
                throw new InvalidOperationException($"{name}={d} 超范围 [{attr.Min}, {attr.Max}]");
            if (value is int i && (i < (int)attr.Min || i > (int)attr.Max))
                throw new InvalidOperationException($"{name}={i} 超范围 [{(int)attr.Min}, {(int)attr.Max}]");
        }
    }
}

/// <summary>可优化参数元数据</summary>
public class ParamMeta
{
    public string Name { get; init; } = "";
    public string Group { get; init; } = "General";
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; }
    public string ValueType { get; init; } = "Double";
}
