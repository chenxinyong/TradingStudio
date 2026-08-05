# 下周待办：TradingStudio 架构加固

> 周四 8/7 — 周三 8/13（5 个工作日）\
> 借鉴 StockSharp & LEAN，主线：FillChannel 竞态根治 → StrategyParam\<T\> → WalkForward 通用化

---

## 🎯 本周目标

| 目标 | 衡量标准 |
|------|---------|
| FillChannel 全链路单消费者，零竞态 | EngineHubPushService + OrderPersistenceService 不再直接读 Channel.Reader |
| `StrategyParam<T>` 替代反射 + object 字典 | MaCross 策略试点，编译期类型安全 |
| WalkForward 不再硬编码参数范围 | 从 StrategyParam.OptimizeRange 自动生成扫描组合 |
| 现有测试全绿 | 280+ tests pass |

---

## Day 1: 周四 8/7 — FillChannel 竞态根治

### 背景分析

[当前架构](src/TradingStudio.Engine/ExecutionHandler.cs#L45-L49) 已用双通道设计解决了主要竞态：

```
CtpTraderBridge  →  FillChannel  →  TradingEngine（唯一消费者 ✅）
                                      ↓ ProcessFill 后
                                   OrderOutbox  →  [HubPushService, PersistenceService]
                                                       ↑
                                                   两个 TryRead 并发，仍会丢事件
```

**残留问题**：[EngineHubPushService](src/TradingStudio/Services/EngineHubPushService.cs#L39-L56) 和 [OrderPersistenceService](src/TradingStudio/Services/OrderPersistenceService.cs#L24-L31) 都在并发 `TryRead` 同一个 `OrderOutbox.Reader`——.NET `ChannelReader.TryRead()` 非线程安全，高并发下会丢事件。这就是 [CLAUDE.md](CLAUDE.md) 里标注的"⚠️ FillChannel 双消费者竞态"。

### 上午：分析 + 设计

- [ ] 确认 `TryRead` 竞态机制：阅读 [PortfolioManager.ProcessFill 的 lock 模式](src/TradingStudio.Engine/PortfolioManager.cs#L292-L298)，验证引擎侧单消费者是否可复用
- [ ] 设计 `IOrderEventSink` 接口
- [ ] 设计 `OrderEventPump`：借鉴 StockSharp [CtpMessageAdapter.PumpCallbacks()](C:\Works\ClaudeCode\StockSharp\Algo.Strategies\Strategy_HighLevelSubscriptions.cs) 的单消费者 → 串行分发模式

### 下午：实现

- [ ] **新建** `src/TradingStudio.Engine/OrderEventPump.cs`

  ```csharp
  /// <summary>
  /// 借鉴 StockSharp CtpMessageAdapter 回调泵：
  /// Channel(单一Reader) → 单消费者Pump → 串行调用多个IOrderEventSink
  /// 消除 OrderOutbox 双消费者竞态。
  /// </summary>
  public class OrderEventPump : IHostedService
  {
      private readonly ChannelReader<OrderEvent> _reader;
      private readonly List<IOrderEventSink> _sinks = new();

      public OrderEventPump(ChannelReader<OrderEvent> reader) { _reader = reader; }
      public void AddSink(IOrderEventSink sink) => _sinks.Add(sink);

      private async Task PumpAsync(CancellationToken ct)
      {
          while (await _reader.WaitToReadAsync(ct).ConfigureAwait(false))
          while (_reader.TryRead(out var e))
              foreach (var sink in _sinks)
                  try { await sink.HandleAsync(e, ct); }
                  catch (Exception ex) { /* log, don't stop pump */ }
      }
  }

  public interface IOrderEventSink
  {
      Task HandleAsync(OrderEvent e, CancellationToken ct);
  }
  ```

- [ ] **改造** [EngineHubPushService](src/TradingStudio/Services/EngineHubPushService.cs#L39-L56)：实现 `IOrderEventSink`，剥离 `Channel.Reader` 直读
- [ ] **改造** [OrderPersistenceService](src/TradingStudio/Services/OrderPersistenceService.cs#L24-L31)：实现 `IOrderEventSink`，剥离 `Channel.Reader` 直读
- [ ] **更新** [LiveComposer.cs](src/TradingStudio/Services/LiveComposer.cs#L242-L246) DI 注册：

  ```diff
  + services.AddSingleton<OrderEventPump>();
  - // EngineHubPushService / OrderPersistenceService 从 HostedService 改为 IOrderEventSink
  - // 注入 OrderEventPump.AddSink() 注册
  ```

---

## Day 2: 周五 8/8 — StrategyParam\<T\> 泛型化

### 现状痛点

| 已有 | 痛点 |
|------|------|
| [`[StrategyParameter]`](src/TradingStudio.Core/Strategy/StrategyParameterAttribute.cs#L1-L16) — Min/Max/Category | 无优化范围字段 |
| [`StrategyParameters`](src/TradingStudio.Core/Strategy/StrategyConfig.cs#L56-L72) | `Dictionary<string, object>` 无类型安全 |
| [`StrategyFactory`](src/TradingStudio.Engine/StrategyFactory.cs#L34-L59) | 反射 + `Convert.ChangeType` → 运行时炸 |
| [`WalkForwardCommand`](src/TradingStudio/Commands/WalkForwardCommand.cs#L393-L453) | 每种策略硬编码参数扫描范围 |

### 上午：StrategyParam\<T\> 实现

- [ ] **新建** `src/TradingStudio.Core/Strategy/StrategyParam.cs`

  ```csharp
  /// <summary>
  /// 强类型策略参数 — 借鉴 StockSharp StrategyParam&lt;T&gt;。
  /// 自带验证、分组、优化范围，替代反射 + object 字典。
  /// </summary>
  public class StrategyParam<T>
  {
      public string Name { get; }
      public string Group { get; init; } = "General";
      public string Description { get; init; } = "";
      public T Value { get; set; }
      public (T Min, T Max, T Step)? OptimizeRange { get; init; }
      public Func<T, bool>? Validator { get; init; }

      public StrategyParam(string name, T defaultValue)
      {
          Name = name;
          Value = defaultValue;
      }

      public static implicit operator T(StrategyParam<T> p) => p.Value;
  }
  ```

- [ ] **试点改造** [MaCrossStrategy](src/TradingStudio.Engine/Examples/MaCrossStrategy.cs#L18-L55)：12 个参数从 `[StrategyParameter]` 属性迁移到 `StrategyParam<T>` 字段

  ```diff
  - [StrategyParameter(Min = 1, Max = 200, Category = "Entry")]
  - public int FastPeriod { get; set; } = 5;
  + public StrategyParam<int> FastPeriod { get; } = new("FastPeriod", 5)
  + {
  +     Group = "Entry",
  +     OptimizeRange = (3, 20, 1),
  +     Validator = v => v > 0
  + };
  ```

- [ ] **更新** [StrategyFactory](src/TradingStudio.Engine/StrategyFactory.cs#L34-L59) `DiscoverParameters()`——同时支持：
  - 新的 `StrategyParam<T>` 字段扫描（`GetFields` + `IsGenericType`）
  - 旧的 `[StrategyParameter]` 属性（兼容其他未改造的策略）

### 下午：WalkForward 通用化 + 测试

- [ ] **重构** [WalkForwardCommand.SelectScanParameters()](src/TradingStudio/Commands/WalkForwardCommand.cs#L393-L453)——从硬编码 switch-case 改为从 `StrategyParam<T>.OptimizeRange` 自动生成笛卡尔积参数组合
- [ ] **实现** `gen-config` 命令：`dotnet run -- gen-config --strategy MaCross → ma-cross-default.json`，从 StrategyParam 元数据生成带注释的 JSON
- [ ] **运行** `dotnet test`，确认 280+ pass，特别是 Engine 150 tests

---

## Day 3-4: 周一-二 8/11-8/12 — Live 交易日观察

### 周一（日盘 8:30-15:30）

- [ ] Live 持续运行，观察项：
  - [ ] `OrderEventPump` 是否稳定运行（无异常日志、CPU 使用正常）
  - [ ] `OrderPersistenceService` DuckDB 写入——Sink 模式下确认不再漏事件（对比 CTP 回报数量 vs DB 落盘数量）
  - [ ] [CtpTraderBridge 自动重连](src/TradingStudio/Live/CtpTraderBridge.cs)（8/5 已修复 `_pendingReconnect` 竞态）——记录重连次数和耗时
  - [ ] `health.json` 各指标正常：quotes / bars / csv / reconnects / uptime

### 周二（日盘 + 夜盘）

- [ ] 夜盘观察：确认 `OrderEventPump` 在夜盘-日盘切换（跨自然日）场景下稳定
- [ ] 如果全天正常，标记 FillChannel 竞态为 **已解决** ✅
- [ ] 更新 [CLAUDE.md](CLAUDE.md) —— FillChannel 架构描述 + StrategyParam\<T\> 用法
- [ ] 写入新的项目状态记忆

---

## Day 5: 周三 8/13 — 缓冲与收尾

| 场景 | 行动 |
|------|------|
| 前面全部顺利 | 继续改造 BollingerReversion / DonchianTrend → StrategyParam\<T\> |
| FillChannel Pump 有 bug | 修复 + 重新测试 → 延迟非关键项 |
| 一切提前完成 | 切到 Tier 2.1：因子管线 C# 化（`IFactor.Process(bar)` 统一接口） |

---

## 📊 进度追踪

### 关键文件清单

| 文件 | 改动类型 | 优先级 |
|------|---------|--------|
| `src/TradingStudio.Engine/OrderEventPump.cs` | **新建** | P0 |
| `src/TradingStudio/Services/EngineHubPushService.cs` | 改造 | P0 |
| `src/TradingStudio/Services/OrderPersistenceService.cs` | 改造 | P0 |
| `src/TradingStudio/Services/LiveComposer.cs` | DI 重构 | P0 |
| `src/TradingStudio.Core/Strategy/StrategyParam.cs` | **新建** | P1 |
| `src/TradingStudio.Engine/Examples/MaCrossStrategy.cs` | 试点改造 | P1 |
| `src/TradingStudio.Engine/StrategyFactory.cs` | 适配 | P1 |
| `src/TradingStudio/Commands/WalkForwardCommand.cs` | 重构 | P1 |
| [CLAUDE.md](CLAUDE.md) | 文档更新 | P2 |

### 借鉴来源

| 改进项 | 来源 | 借鉴了什么 |
|--------|------|-----------|
| `OrderEventPump` | StockSharp `CtpMessageAdapter.PumpCallbacks()` | Channel 单消费者 → 串行 Sink 模式 |
| `IOrderEventSink` | StockSharp `Enqueue` + 回调委托列表 | 处理器注册/异常隔离 |
| `StrategyParam<T>` | StockSharp `StrategyParam<T>` | 泛型参数 + OptimizeRange + Validator |
| `WalkForward 通用化` | LEAN `QCAlgorithm.SetParameters()` | 参数元数据驱动扫描，非硬编码 |

---

## ❌ 本周不做的

| 事项 | 原因 |
|------|------|
| 因子管线 C# 化（`IFactor` 统一接口） | Tier 2.1，等 FillChannel + Param 稳定后切入 |
| TradeSignal / PortfolioTarget 解耦 | 需要更多设计讨论，属架构升级 Tier 2.2 |
| 连续合约双旋钮（DataMappingMode × DataNormalizationMode） | 无紧迫需求，等做复权对比研究时再做 |
| Designer / Terminal / Hydra 集成 | 你有 WPF 终端 + DuckDB，不需要 |
| MaCross 以外的策略批量改造 | 先试点验证，稳定后再批量迁移 |

---

> **Why**：两个 T1 项都是"改动小、效果大"的高杠杆改进——FillChannel Pump 消除最后的竞态隐患；StrategyParam\<T\> 让参数系统从反射 + object 字典升级到编译期类型安全，WalkForward 不再需要为新策略修改命令代码。
>
> **How to apply**：周四全天 FillChannel（影响生产稳定性的最高优项），周五 StrategyParam（提升策略可维护性），周初观察 Live 稳定性，周三做缓冲。如果 FillChannel 两天不够，StrategyParam 顺延，不阻塞主修复。
