# StrategyFactory — 策略注册与实例化

## 调用链

```
BacktestCommand / Program.RunLiveAsync
  │
  ├─ ① StrategyFactory.DiscoverFromAssembly(assembly)
  │     扫描程序集中所有 IStrategy 实现
  │     MaCrossStrategy → 去掉 "Strategy" 后缀 → "MaCross"
  │     注册到 _registry["MaCross"] = typeof(MaCrossStrategy)
  │
  ├─ ② StrategyFactory.Create(config)
  │     │ _registry[config.StrategyType] → Type
  │     │ Activator.CreateInstance(type) → IStrategy
  │     │
  │     ├─ ③ DiscoverParameters(type)
  │     │     反射扫描所有带 [StrategyParameter] 的 PropertyInfo
  │     │     → Dictionary<string, PropertyInfo>
  │     │
  │     ├─ ④ 参数注入
  │     │     foreach (key, value) in config.Parameters
  │     │       prop.SetValue(strategy, Convert.ChangeType(value, prop.PropertyType))
  │     │
  │     └─ ⑤ ValidateParameters
  │           检查 Min ≤ value ≤ Max, Required 必填
  │
  └─ ⑥ return strategy → EngineStrategyContext → strategy.Initialize(ctx)
```

## 注册方式

### 自动发现（推荐）

```csharp
// 回测命令中扫描两个程序集
StrategyFactory.DiscoverFromAssembly(typeof(TradingEngine).Assembly);      // Engine/Examples
StrategyFactory.DiscoverFromAssembly(typeof(ChanLunStrategy).Assembly);    // Strategy 项目
```

自动命名规则: `类名.Replace("Strategy", "")`

### 手动注册

```csharp
StrategyFactory.Register<MyCustomStrategy>("MyCustom");
```

## 参数注入示例

策略定义:
```csharp
[StrategyParameter(Description = "快线周期", DefaultValue = 5, Min = 2, Max = 60)]
public int FastPeriod { get; set; } = 5;
```

JSON 配置:
```json
{ "Parameters": { "FastPeriod": 10 } }
```

注入: `Convert.ChangeType("10", typeof(int)) → property.SetValue(strategy, 10)`

## StrategyParameter 属性

```csharp
public class StrategyParameterAttribute : Attribute
{
    public string Description { get; init; }    // 参数说明 (UI 显示)
    public object DefaultValue { get; init; }   // 默认值 (策略类字段初始值)
    public double Min { get; init; }            // 最小值
    public double Max { get; init; }            // 最大值
    public bool Required { get; init; }         // 是否必填
    public string Category { get; init; }       // UI 分组: Entry/Exit/Filter/Risk/Position
}
```

## 验证失败行为

| 失败类型 | 异常 | 示例 |
|----------|------|------|
| 策略类型未注册 | `InvalidOperationException` | `StrategyType: "Unknown"` |
| 参数超出范围 | `InvalidOperationException` | `FastPeriod=1 (Min=2)` |
| Required 参数缺失 | `InvalidOperationException` | `Required=true` 但未在 JSON 提供 |
