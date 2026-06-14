using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine;

/// <summary>
/// 策略工厂 — 显式注册表 + 参数注入。
/// 策略类型必须预先注册，不依赖程序集扫描。
/// </summary>
public static class StrategyFactory
{
    private static readonly Dictionary<string, Type> _registry = new();

    public static void Register<T>(string name) where T : IStrategy
        => _registry[name] = typeof(T);

    /// <summary>自动发现程序集中所有 IStrategy 实现并注册（类名去掉 Strategy 后缀）</summary>
    public static void DiscoverFromAssembly(System.Reflection.Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type is { IsAbstract: false, IsInterface: false } && typeof(IStrategy).IsAssignableFrom(type))
            {
                var name = type.Name;                         // "MaCrossStrategy"
                if (name.EndsWith("Strategy"))
                    name = name[..^8];                        // "MaCross"
                if (!_registry.ContainsKey(name))
                    _registry[name] = type;
            }
        }
    }

    public static IReadOnlyList<string> RegisteredNames => _registry.Keys.OrderBy(k => k).ToList();

    public static IStrategy Create(StrategyConfig config)
    {
        if (!_registry.TryGetValue(config.StrategyType, out var type))
            throw new InvalidOperationException(
                $"未注册的策略: {config.StrategyType}。请先调用 StrategyFactory.Register<{config.StrategyType}>(\"{config.StrategyType}\")");

        // 实例化（策略类需要无参构造函数；初始化通过 Initialize 完成）
        var strategy = (IStrategy)Activator.CreateInstance(type)!;

        // 应用配置覆盖
        var parameters = DiscoverParameters(type);
        foreach (var (key, value) in config.Parameters)
        {
            if (parameters.TryGetValue(key, out var prop))
                prop.SetValue(strategy, Convert.ChangeType(value, prop.PropertyType));
        }

        // 验证参数范围
        ValidateParameters(strategy, parameters, config.Parameters);

        return strategy;
    }

    private static Dictionary<string, System.Reflection.PropertyInfo> DiscoverParameters(Type type)
    {
        var result = new Dictionary<string, System.Reflection.PropertyInfo>();
        foreach (var prop in type.GetProperties())
        {
            var attr = prop.GetCustomAttributes(typeof(StrategyParameterAttribute), true)
                .FirstOrDefault() as StrategyParameterAttribute;
            if (attr != null)
                result[prop.Name] = prop;
        }
        return result;
    }

    private static void ValidateParameters(
        IStrategy strategy,
        Dictionary<string, System.Reflection.PropertyInfo> parameters,
        StrategyParameters configParams)
    {
        foreach (var (name, prop) in parameters)
        {
            var attr = (StrategyParameterAttribute)prop.GetCustomAttributes(
                typeof(StrategyParameterAttribute), true).First();

            var value = prop.GetValue(strategy);
            if (attr.Required && value == null)
                throw new InvalidOperationException($"策略 {strategy.GetType().Name} 缺少必需参数: {name}");

            if (value is double d)
            {
                if (d < attr.Min || d > attr.Max)
                    throw new InvalidOperationException(
                        $"参数 {name}={d} 超出范围 [{attr.Min}, {attr.Max}]");
            }
            if (value is int i)
            {
                if (i < (int)attr.Min || i > (int)attr.Max)
                    throw new InvalidOperationException(
                        $"参数 {name}={i} 超出范围 [{(int)attr.Min}, {(int)attr.Max}]");
            }
        }
    }
}
