# StrategyFactory — 策略注册与实例化 (v2)

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
  │     ├─ ③ 参数注入 — 双模式扫描 (v2)
  │     │     │
  │     │     ├─ 新 (推荐): ScanParamFields — 反射扫描 StrategyParam<T> 字段
  │     │     │     field.FieldType.GetGenericTypeDefinition() == typeof(StrategyParam<>)
  │     │     │     → field.FieldType.GetProperty("Value").SetValue(...)
  │     │     │
  │     │     └─ 旧 (兼容): ScanParamProps — 反射扫描 [StrategyParameter] 属性
  │     │           → prop.SetValue(strategy, Convert.ChangeType(value, prop.PropertyType))
  │     │
  │     └─ ④ Validate — 双模式验证
  │           │ StrategyParam<T>: Validator委托 (如 v => v > 0)
  │           └─ [StrategyParameter]: Min ≤ value ≤ Max, Required 非 null
  │
  └─ ⑤ return strategy → EngineStrategyContext → strategy.Initialize(ctx)
```

### v2 新增: GetOptimizableParams()

```csharp
// WalkForwardCommand / 参数扫描工具使用
var strategy = StrategyFactory.Create(config);
var metas = StrategyFactory.GetOptimizableParams(strategy);
// → List<ParamMeta> { Name, Group, Min, Max, Step, ValueType }
// 来源: StrategyParam<T>.OptimizeRange (ValueTuple)
```

## 注册方式

### 自动发现（推荐）

```csharp
StrategyFactory.DiscoverFromAssembly(typeof(TradingEngine).Assembly);      // Engine/Examples
StrategyFactory.DiscoverFromAssembly(typeof(ChanLunStrategy).Assembly);    // Strategy 项目
```

自动命名规则: `类名.Replace("Strategy", "")`

### 手动注册

```csharp
StrategyFactory.Register<MyCustomStrategy>("MyCustom");
```

## 参数模式 (v2 推荐: StrategyParam\<T\>)

### 新风格 (借鉴 StockSharp)

```csharp
public StrategyParam<int> FastPeriod { get; } = new("FastPeriod", 10)
    { Group = "Entry", Description = "快线周期", OptimizeRange = (2, 60, 1) };
```

- 编译期类型安全, 隐式转换 `operator T` 兼容旧代码
- `OptimizeRange` 声明可参与 WalkForward 参数扫描
- `Validator` 可附加自定义验证

JSON 配置不变: `{ "Parameters": { "FastPeriod": 10 } }`

### 旧风格 (兼容)

```csharp
[StrategyParameter(Description = "快线周期", DefaultValue = 5, Min = 2, Max = 60)]
public int FastPeriod { get; set; } = 5;
```

## StrategyParameter 属性 (旧风格)

```csharp
public class StrategyParameterAttribute : Attribute
{
    public string Description { get; init; }
    public object DefaultValue { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public bool Required { get; init; }
    public string Category { get; init; }
}
```

## 验证失败行为

| 失败类型 | 异常 | 示例 |
|----------|------|------|
| 策略类型未注册 | `InvalidOperationException` | `StrategyType: "Unknown"` |
| 参数超出范围 (旧) | `InvalidOperationException` | `FastPeriod=1 (Min=2)` |
| Validator 失败 (新) | `InvalidOperationException` | `v => v > 0` |
| Required 参数缺失 | `InvalidOperationException` | `Required=true` 但未在 JSON 提供 |
