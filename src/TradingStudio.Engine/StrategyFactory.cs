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
            if (paramFields.TryGetValue(key, out var fi))
                ApplyParamField(strategy, fi, value);
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

    private static Dictionary<string, FieldInfo> ScanParamFields(IStrategy strategy)
    {
        var result = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in strategy.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!field.FieldType.IsGenericType) continue;
            if (field.FieldType.GetGenericTypeDefinition() != typeof(StrategyParam<>)) continue;

            var instance = field.GetValue(strategy);
            var name = field.FieldType.GetProperty("Name")?.GetValue(instance)?.ToString();
            if (!string.IsNullOrEmpty(name)) result[name] = field;
        }
        return result;
    }

    private static void ApplyParamField(IStrategy strategy, FieldInfo field, object value)
    {
        var instance = field.GetValue(strategy);
        if (instance == null) return;
        var valueProp = field.FieldType.GetProperty("Value");
        var targetType = field.FieldType.GetGenericArguments()[0];
        valueProp?.SetValue(instance, Convert.ChangeType(value, targetType));
    }

    private static void ValidateParamFields(IStrategy strategy, Dictionary<string, FieldInfo> fields)
    {
        foreach (var (name, field) in fields)
        {
            var instance = field.GetValue(strategy);
            if (instance == null) continue;

            var validator = field.FieldType.GetProperty("Validator")?.GetValue(instance) as Delegate;
            if (validator == null) continue;

            var current = field.FieldType.GetProperty("Value")?.GetValue(instance);
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
        foreach (var (_, field) in ScanParamFields(strategy))
        {
            var instance = field.GetValue(strategy);
            if (instance == null) continue;

            var range = field.FieldType.GetProperty("OptimizeRange")?.GetValue(instance);
            if (range == null) continue;

            var name = field.FieldType.GetProperty("Name")?.GetValue(instance)?.ToString() ?? "";
            var group = field.FieldType.GetProperty("Group")?.GetValue(instance)?.ToString() ?? "General";

            // ValueTuple (Min, Max, Step)
            var rt = range.GetType();
            var min = rt.GetProperty("Item1")?.GetValue(range);
            var max = rt.GetProperty("Item2")?.GetValue(range);
            var step = rt.GetProperty("Item3")?.GetValue(range);

            if (min != null && max != null)
                result.Add(new ParamMeta
                {
                    Name = name, Group = group,
                    Min = Convert.ToDouble(min),
                    Max = Convert.ToDouble(max),
                    Step = step != null ? Convert.ToDouble(step) : 0,
                    ValueType = field.FieldType.GetGenericArguments()[0].Name,
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
