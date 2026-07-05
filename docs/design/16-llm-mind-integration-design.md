# TradingStudio.Mind — LLM 集成最小可行设计

> Phase 3a MVP：回测报告 LLM 分析。不做大而全的"AI Bloomberg Terminal"，从一个能跑通的分析场景开始。

---

## 1. 定位与边界

### Mind 是什么

```
TradingStudio.Mind = LLM 分析层，不是交易决策层

它做的事情：
  ✅ 解读回测报告，给出多维度诊断
  ✅ 解读缠论结构（分型/笔/中枢 → 人话）
  ✅ 回答关于策略行为的自然语言问题
  ✅ 生成策略研究报告

它不做的事情：
  ❌ 不直接下单（LLM 的输出不过风控引擎）
  ❌ 不替换策略逻辑（策略仍然是确定性的 C# 代码）
  ❌ 不做实时高频分析（那是规则引擎的事）
```

### 与现有系统的关系

```
PerformanceReport ──┐
StrategyConfig    ──┼──→ TradingStudio.Mind ──→ Markdown 分析报告
Trade[]           ──┤                           JSON 结构化结果
ChanLunResult     ──┘

Mind 是"只读消费者"——它读取引擎输出，生成分析，但从不写回交易管线。
```

---

## 2. 架构概览

```
┌──────────────────────────────────────────────────────┐
│                  TradingStudio.Mind                   │
│                                                       │
│  ┌─────────────┐   ┌──────────────┐   ┌───────────┐  │
│  │   Analysts   │   │   Prompts    │   │  Clients  │  │
│  │              │   │              │   │           │  │
│  │ Backtest     │──→│ SystemPrompt │──→│ Anthropic │  │
│  │ Analyst      │   │ + Template   │   │ Client    │  │
│  │              │   │              │   │           │  │
│  │ ChanLun      │   │ Context      │   │ OpenAI    │  │
│  │ Analyst      │   │ Assembler    │   │ Client    │  │
│  └──────┬───────┘   └──────────────┘   └─────┬─────┘  │
│         │                                     │        │
│         │  ILLMClient                         │        │
│         └─────────────────────────────────────┘        │
│                                                       │
│  输入: PerformanceReport / StrategyConfig / Trade[]    │
│  输出: MindAnalysisResult (.md + .json)                │
└──────────────────────────────────────────────────────┘
```

三层结构：
- **Analysts** — 领域分析逻辑。知道"一份回测报告该问 LLM 什么问题"
- **Prompts** — 提示词模板 + 上下文组装。知道"如何把 Trading 数据变成 LLM 能理解的文本"
- **Clients** — LLM 提供商封装。知道"怎么调 Anthropic/OpenAI API"

---

## 3. 核心接口

### 3.1 ILLMClient — LLM 调用的最小抽象

```csharp
// src/TradingStudio.Mind/Clients/ILLMClient.cs

namespace TradingStudio.Mind.Clients;

/// <summary>LLM 客户端最小抽象。一个方法够用，后面再扩展。</summary>
public interface ILLMClient
{
    /// <summary>
    /// 发送消息列表，返回 LLM 文本回复。
    /// messages[0] 通常是 system，后续是 user/assistant 交替。
    /// </summary>
    Task<string> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default);
}

public record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
    public static ChatMessage Assistant(string content) => new("assistant", content);
}

public record ChatOptions
{
    public string? Model { get; init; }           // 覆盖默认模型
    public int MaxTokens { get; init; } = 4096;
    public double Temperature { get; init; } = 0.3;
}
```

### 3.2 IMindAnalyst — 分析操作的门面

```csharp
// src/TradingStudio.Mind/Analysts/IMindAnalyst.cs

namespace TradingStudio.Mind.Analysts;

public interface IMindAnalyst
{
    /// <summary>分析类型标识，用于日志和结果归档</summary>
    string AnalysisType { get; }
}

/// <summary>回测分析结果</summary>
public record BacktestAnalysisResult
{
    public string StrategyId { get; init; } = "";
    public string MarkdownReport { get; init; } = "";    // 人类可读
    public BacktestDiagnosis? Diagnosis { get; init; }    // 结构化诊断（可选）
}

/// <summary>结构化诊断 — 从 LLM 回复中提取的关键发现</summary>
public record BacktestDiagnosis
{
    public string OverallAssessment { get; init; } = "";  // 综合评价（1-2句）
    public List<string> Strengths { get; init; } = [];    // 策略优点
    public List<string> Weaknesses { get; init; } = [];   // 策略缺陷
    public List<string> Suggestions { get; init; } = [];  // 优化建议
    public string RiskLevel { get; init; } = "";          // "低"/"中"/"高"
}
```

### 3.3 IContextAssembler — 数据压缩（最关键的设计）

```csharp
// src/TradingStudio.Mind/Prompts/IContextAssembler.cs

namespace TradingStudio.Mind.Prompts;

/// <summary>
/// 上下文组装器 — 把 Trading 领域数据压缩成 LLM 上下文窗口装得下的文本。
/// 这是 Mind 中最关键的环节：8100 万 Bar 不能塞进去，需要"先聚合、再送入"。
/// </summary>
public interface IContextAssembler<TInput>
{
    /// <summary>组装 LLM 上下文。返回的字符串应该在 token 预算内。</summary>
    ContextAssemblyResult Assemble(TInput input, ContextBudget budget);
}

public record ContextBudget
{
    /// <summary>上下文窗口总 token 数（默认 100K，留 20K 给回复）</summary>
    public int MaxInputTokens { get; init; } = 80_000;

    /// <summary>各部分预算分配比例</summary>
    public double SystemPromptRatio { get; init; } = 0.05;    // 5%
    public double DataSectionRatio { get; init; } = 0.70;     // 70%
    public double QuestionRatio { get; init; } = 0.25;        // 25%
}

public record ContextAssemblyResult
{
    public string SystemPrompt { get; init; } = "";
    public string DataContext { get; init; } = "";   // 压缩后的交易数据
    public string UserQuestion { get; init; } = "";  // 分析问题
    public int EstimatedTokens { get; init; }
}
```

---

## 4. 数据流 —— 一次完整的回测分析

```
输入                            组装                              LLM                      输出
════                            ═══                              ═══                      ══

PerformanceReport  ─┐
  .StrategyId        │
  .CAGR (12.5%)      │
  .Sharpe (1.2)      │    BacktestContextAssembler
  .MaxDrawdown (18%) │    ┌─────────────────────┐
  .WinRate (42%)     │    │ SystemPrompt:       │
  .ProfitLossRatio   │───→│ "你是一位量化交易   │    AnthropicClient       MindAnalysisResult
  .TotalFees         │    │  策略分析师..."     │    ┌──────────────┐      ┌──────────────────┐
  .EquityCurve       │    │                     │    │ POST /v1/    │      │ MarkdownReport:  │
                     │    │ DataContext:        │    │   messages   │─────→│ "## 策略诊断     │
StrategyConfig ──────┤    │ "## 策略概况        │    │              │      │                  │
  .StrategyType      │    │  - 类型: 唐奇安     │    │ {            │      │ 优势:            │
  .Instruments       │    │  - 品种: RB,HC      │    │   model,     │      │ 1. Sharpe 1.2 ..│
  .Parameters        │    │  - 回测期: 2023-    │    │   messages   │      │                  │
                     │    │   2025              │    │ }            │      │ 缺陷:            │
Trade[] ─────────────┤    │                     │    └──────────────┘      │ 1. 盈亏比偏低.. │
  Top 5 wins         │    │ ## 绩效摘要          │                          │                  │
  Top 5 losses       │    │ | 指标 | 值 |       │                          │ 建议:            │
  Monthly PnL        │    │ | CAGR | 12.5% ...  │                          │ 1. 加波动率过滤 │
                     │    │                     │                          │                  │
                     │    │ ## 典型交易         │                          │ Diagnosis:       │
                     │    │ ### 最佳5笔         │                          │ { Strengths:[],  │
                     │    │ ...                 │                          │   Weaknesses:[]} │
                     │    │                     │                          └──────────────────┘
                     │    │ ## 月度收益分布     │
                     │    │ ...                 │
                     │    │                     │
                     │    │ UserQuestion:       │
                     │    │ "请分析该策略的      │
                     │    │  风险收益特征..."    │
                     │    └─────────────────────┘
                     │
                     │ Token 预算: ~80K input
                     │ System:    ~4K
                     │ Data:     ~56K
                     │ Question: ~20K
```

### 上下文组装策略（关键设计决策）

```
Bar 数据不直接送入 LLM，而是：
  1min Bar (原始)  →  不送入（太细，上下文窗口装不下）
  5min/15min Bar   →  聚合为技术指标摘要（MA 位置、布林带宽度、ATR）
  Day Bar          →  聚合为日收益分布
  权益曲线         →  降采样到月度数据点 + 关键回撤期定位
  Trade 列表       →  Top N + Bottom N 抽样 + 按月的统计摘要
  ChanLun 结构     →  分型/笔/中枢的文本化描述（不是原始数据）

策略配置          →  完整送入（通常 < 2KB）
```

---

## 5. 项目结构

```
src/TradingStudio.Mind/
├── TradingStudio.Mind.csproj       ← 引用 Core + Engine，不引用 Data
├── MindOptions.cs                  ← 配置类
│
├── Clients/
│   ├── ILLMClient.cs               ← 接口 + ChatMessage/ChatOptions
│   ├── AnthropicClient.cs          ← Anthropic Messages API
│   └── OpenAiClient.cs             ← OpenAI Chat Completions API（后续）
│
├── Prompts/
│   ├── IContextAssembler.cs        ← 接口 + ContextBudget/ContextAssemblyResult
│   ├── BacktestContextAssembler.cs ← 回测数据 → LLM 上下文
│   └── Templates/                  ← 内嵌的 prompt 模板（Phase 3a 用 const string）
│       └── BacktestAnalysis.cs     ← System prompt + 分析框架
│
├── Analysts/
│   ├── IMindAnalyst.cs             ← 分析结果类型
│   ├── BacktestAnalyst.cs          ← 回测分析
│   └── ChanLunAnalyst.cs           ← 缠论结构解读（Phase 3b）
│
└── Commands/
    └── AnalyzeCommand.cs           ← CLI 入口：dotnet run -- analyze
```

### 依赖关系

```
TradingStudio.Mind
  ├── TradingStudio.Core       (Bar, Trade, StrategyConfig, Future)
  ├── TradingStudio.Engine     (PerformanceReport, EngineReport)
  └── Microsoft.Extensions.*   (DI, Logging, Configuration — 已有)
      System.Text.Json          (已有)
      System.Net.Http           (已有)

不依赖:
  ├── TradingStudio.Data       (不需要直接访问 DuckDB)
  └── 任何第三方 AI SDK        (直接调 HTTP API)
```

**为什么不需要 Semantic Kernel / LangChain？**

第一阶段只有一个用例（发送 prompt → 获取回复）。直接调 HTTP API 约 100 行代码。引入 Semantic Kernel 至少在 500 行配置 + 额外的学习成本。等需要 Function Calling / Tool Use 时再引入。

---

## 6. AnthropicClient 实现要点

```csharp
// 核心逻辑 ~80 行
public class AnthropicClient : ILLMClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _defaultModel;

    public async Task<string> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var body = new
        {
            model = options?.Model ?? _defaultModel,
            max_tokens = options?.MaxTokens ?? 4096,
            temperature = options?.Temperature ?? 0.3,
            system = messages.Where(m => m.Role == "system")
                .Select(m => new { type = "text", text = m.Content }).ToList(),
            messages = messages.Where(m => m.Role != "system")
                .Select(m => new { role = m.Role, content = m.Content }).ToList()
        };

        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.anthropic.com/v1/messages")
        {
            Headers = {
                { "x-api-key", _apiKey },
                { "anthropic-version", "2023-06-01" }
            },
            Content = JsonContent.Create(body)
        };

        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }
}
```

**设计要点：**
- System prompt 走顶层 `system` 字段（Anthropic 特有），不是 `messages[0].role="system"`
- `x-api-key` header 认证，不需要 OAuth
- 流式响应（SSE）留到 Phase 3c，第一版只用非流式

---

## 7. 配置设计

```jsonc
// appsettings.json 或 mind.json
{
  "Mind": {
    "Provider": "Anthropic",           // "Anthropic" | "OpenAI"
    "DefaultModel": "claude-sonnet-5", // 或其他
    "MaxTokens": 4096,
    "Temperature": 0.3,                // 分析类任务要低温度

    "Anthropic": {
      "ApiKey": "${ANTHROPIC_API_KEY}", // 环境变量，不写死在配置里
      "BaseUrl": "https://api.anthropic.com/v1"
    },
    "OpenAI": {
      "ApiKey": "${OPENAI_API_KEY}",
      "BaseUrl": "https://api.openai.com/v1"
    }
  }
}
```

### API Key 安全约定

```
优先级:
1. 环境变量 ANTHROPIC_API_KEY / OPENAI_API_KEY
2. mind.json 中的 ApiKey 字段（仅开发环境）
3. 不在 git 中提交任何 API Key

.gitignore 添加:
  **/mind.local.json
```

---

## 8. Phase 3a 实现计划（~10 小时）

### Step 1: 项目脚手架 (30 min)

```bash
dotnet new classlib -n TradingStudio.Mind -o src/TradingStudio.Mind
dotnet sln add src/TradingStudio.Mind
```

创建文件骨架：
- `MindOptions.cs`
- `Clients/ILLMClient.cs`
- `Clients/AnthropicClient.cs`
- `Prompts/IContextAssembler.cs`
- `Prompts/BacktestContextAssembler.cs`
- `Analysts/IMindAnalyst.cs`
- `Analysts/BacktestAnalyst.cs`

### Step 2: AnthropicClient (1.5 hr)

- 实现 `ChatAsync` 方法
- 错误处理：401（Key 错）、429（限流重试）、5xx（退避重试）
- 3 次重试，指数退避（1s/2s/4s）
- 单元测试（Mock HttpClient）

### Step 3: BacktestContextAssembler (2 hr) ← 最关键的步骤

这是 Mind 的"灵魂"——如何把 PerformanceReport + Trade[] 变成 LLM 能理解的文本。

```csharp
public ContextAssemblyResult Assemble(
    PerformanceReport report,
    StrategyConfig config,
    ContextBudget budget)
{
    // 1. System prompt (~4K tokens)
    var systemPrompt = """
        你是一位量化交易策略分析师。你的任务是分析回测报告，
        找出策略的优势和缺陷，提出具体可操作的优化建议。
        
        分析框架：
        1. 收益维度 — CAGR、总收益、收益稳定性
        2. 风险维度 — 最大回撤、回撤持续期、下行波动
        3. 交易维度 — 胜率、盈亏比、手续费占比
        4. 稳定性维度 — 月度收益分布、连续亏损次数
        
        请用中文回复。先给一句话综合评价，再分优势和缺陷展开，
        最后给出 3-5 条具体的优化建议。
        """;

    // 2. 策略概况 (~2K tokens)
    var overview = $$"""
        ## 策略概况
        - 策略ID: {{report.StrategyId}}
        - 策略类型: {{config.StrategyType}}
        - 交易品种: {{string.Join(", ", config.Instruments)}}
        - 回测区间: {{report.EquityCurve[0].Time:yyyy-MM-dd}} ~ {{report.EquityCurve[^1].Time:yyyy-MM-dd}}
        - 初始资金: {{report.StartingCapital:N2}}
        - 策略参数: {{FormatParameters(config.Parameters)}}
        """;

    // 3. 绩效指标表 (~1K tokens)
    var metrics = FormatMetricsTable(report);

    // 4. 权益曲线摘要 (~2K tokens)
    var equity = FormatEquitySummary(report.EquityCurve);

    // 5. 最佳/最差交易样本 (~3K tokens)
    var trades = FormatTradeSamples(report.Trades, topN: 5, bottomN: 5);

    // 6. 月度收益分布 (~2K tokens)
    var monthly = FormatMonthlyBreakdown(report.EquityCurve);

    // 7. 分析问题 (~2K tokens)
    var question = """
        ## 分析任务
        
        请从以下维度分析该策略：
        1. 收益特征：CAGR 是否达标？收益是否稳定？
        2. 风险特征：最大回撤是否可控？回撤期集中在什么时段？
        3. 交易效率：WinRate × ProfitLossRatio 是否构成正期望值？
        4. 成本敏感性：手续费和滑点占比是否合理？
        5. 改进方向：针对发现的缺陷，提出具体可行的改进方向。
        
        请用结构化格式回复，每部分用小标题分隔。
        """;

    return new ContextAssemblyResult
    {
        SystemPrompt = systemPrompt,
        DataContext = overview + metrics + equity + trades + monthly,
        UserQuestion = question,
        EstimatedTokens = EstimateTokens(systemPrompt + overview + metrics + ...)
    };
}
```

### Step 4: BacktestAnalyst (1.5 hr)

```csharp
public class BacktestAnalyst
{
    private readonly ILLMClient _client;
    private readonly BacktestContextAssembler _assembler;

    public async Task<BacktestAnalysisResult> AnalyzeAsync(
        PerformanceReport report,
        StrategyConfig config,
        CancellationToken ct = default)
    {
        // 1. 组装上下文
        var ctx = _assembler.Assemble(report, config, new ContextBudget());

        // 2. 构建消息
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(ctx.SystemPrompt),
            ChatMessage.User(ctx.DataContext + "\n\n" + ctx.UserQuestion)
        };

        // 3. 调用 LLM
        var markdown = await _client.ChatAsync(messages, ct: ct);

        // 4. 尝试解析结构化诊断（best-effort，不阻断）
        var diagnosis = TryParseDiagnosis(markdown);

        return new BacktestAnalysisResult
        {
            StrategyId = report.StrategyId,
            MarkdownReport = markdown,
            Diagnosis = diagnosis
        };
    }
}
```

### Step 5: CLI 集成 (1 hr)

```bash
# 独立分析已有报告
dotnet run --project TradingStudio.Mind -- analyze \
    --report results/SmaMacd_RB000.report.json \
    --config configs/SmaMacd_RB000.json

# 输出
# results/SmaMacd_RB000.analysis.md     ← 人类可读
# results/SmaMacd_RB000.analysis.json   ← 结构化诊断
```

### Step 6: 验证测试 (1.5 hr)

- 取一份已有的回测报告（如 DonchianTrend 的某次运行）
- 跑一遍完整流程
- 人工评审 LLM 输出的质量和准确性
- 调整 prompt 直到输出可用的分析

### Step 7: 集成到回测管线 (1 hr)

在 `BacktestCommand` 末尾，可选调用 Mind：

```csharp
// BacktestCommand.cs 末尾
if (options.EnableAiAnalysis)
{
    var analyst = services.GetRequiredService<BacktestAnalyst>();
    var result = await analyst.AnalyzeAsync(perfReport, strategyConfig);
    await File.WriteAllTextAsync(reportPath.Replace(".json", ".analysis.md"),
        result.MarkdownReport);
}
```

---

## 9. 后续 Phase 规划

### Phase 3b: ChanLun 结构解读

```
输入: ChanLunResult (Fractal[], Bi[], Zhongshu[])
输出: "当前日线级别下跌趋势，已形成两个中枢，
       第二个中枢后出现底背驰，30分钟级别三买确认中"
```

策略：把 ChanLun 结构对象序列化为文本描述，让 LLM 解读买卖点。

### Phase 3c: NL2SQL 查询

```
输入: "RB 2023年所有周线大阳线" → LLM → SQL → DuckDB → 结果
```

需要：DuckDB Schema 描述 + few-shot 示例 → 喂给 LLM → 生成 SQL → BarQueryHelper 执行。

### Phase 3d: 实时诊断

```
输入: FeedbackMonitor 告警流 → LLM → 根因分析
"策略 DonchianTrend_RB 在 10:30 停止交易，原因是连续3笔止损触发风控暂停"
```

需要：事件流管道 + 上下文窗口管理（滚动最近 N 个事件）。

---

## 10. 不做的事情（明确边界）

| 不做 | 原因 |
|------|------|
| LLM 自动下单 | 风控不可绕过，LLM 输出不接入交易管线 |
| 实时行情 LLM 分析 | 延迟要求高，LLM 推理太慢 |
| RAG / 向量数据库 | Phase 3 不需要，策略文档不到 100 篇 |
| 多 Agent 辩论 | 过度设计，先让单 Agent 分析跑通 |
| Fine-tuning 模型 | 没有足够的标注数据，预训练模型 + 好的 prompt 已够用 |
| AI 生成策略代码 | 生成的代码不可信，必须人工验证后才上线 |

---

## 11. 关键技术决策

| 决策 | 选择 | 理由 |
|------|------|------|
| LLM 客户端 | 直接 HTTP API | 1 个 API 调用不需要框架，~100 行代码 |
| 提供商 | Anthropic Claude 优先 | Claude Code 已有 API Key，零额外成本 |
| 上下文窗口管理 | 静态预算分配 | 不需要动态裁剪，Phase 3a 数据量可控 |
| 输出格式 | Markdown + JSON | Markdown 给人看，JSON 给程序用 |
| 与回测集成 | CLI + 可选库调用 | 解耦，可以先独立使用再嵌入 |
| Prompt 管理 | const string（第一版） | 文件模板留到 Phase 3b |

---

## 12. 风险与缓解

| 风险 | 缓解 |
|------|------|
| LLM 幻觉——给出看似合理但错误的建议 | 输出始终标注"AI 分析，仅供参考"，关键建议需要人工验证 |
| API 成本 | Phase 3a 每次分析 ~10K tokens × $3/MTok ≈ $0.03，可忽略 |
| Prompt 质量不够，输出泛泛而谈 | 迭代 prompt 直到输出有具体、可操作的建议 |
| C# 调 LLM API 资料少 | Anthropic Messages API 就是 REST，与语言无关 |

---

## 总结

**TradingStudio.Mind Phase 3a 是一个 10 小时可完成的 MVP。** 它不需要新依赖、不需要数据库变更、不需要改动现有引擎。它做的事情很简单但很有价值：**让你在回测完成后，不只是看到一串数字，而是得到一份有洞察的分析报告。**

核心设计理念：
- **只读消费者** — Mind 不写入交易管线
- **先聚合再送入** — 绝不把原始 Bar 数据塞进 LLM
- **简单到可以删掉重写** — 3 个接口、1 个实现、~500 行 C# 代码
- **C# 原生** — 不引入 Python，不引入重框架
