# Log 与运行反馈设计

> 7×24 无人值守。人不在场，Log 是唯一的信息源。

---

## 1. 日志分层

```
Layer 1: 结构化运行日志 → 文件（滚动）          — 运维排错
Layer 2: 关键事件日志   → PostgreSQL            — 审计追溯 + 报表
Layer 3: 实时运行反馈   → SignalR 推送          — WPF/Vue 监控面板
Layer 4: 紧急告警       → 手机通知              — 人不在场时的最后防线
```

---

## 2. 日志级别与使用规则

| 级别 | 含义 | 示例 | 触发动作 |
|---|---|---|---|
| **Trace** | 逐 tick 细节 | 每个 tick 的 LastPrice | 不开启（量太大） |
| **Debug** | 开发调试 | Bar 闭合细节、策略计算中间值 | 开发环境开启 |
| **Info** | 正常运行事件 | 系统启动、CTP 连接成功、日切换、Bar 写入 | 写入文件 + PG |
| **Warning** | 需关注但不紧急 | Tick 密度低于预期、PG 写入延迟偏高、重连成功 | 写入文件 + 推送前端 |
| **Error** | 需尽快处理 | CTP 断连超 5 分钟、PG 写入失败、下单失败 | 写入文件 + 推送 + 报警 |
| **Critical** | 资金安全相关 | 风控触发、仓位异常、连续下单失败 | 写入文件 + PG + 电话报警 |

---

## 3. 结构化 Log 模型

```
传统日志: "2026-06-09 09:01:00 [INFO] Bar closed: rb2510 O=3520 C=3522 V=156"
              ↑ 人眼可读，但程序无法搜索/聚合

结构化日志: 
  {
    "ts": "2026-06-09T09:01:00.123Z",
    "level": "Info",
    "category": "DataPipeline.Bar",
    "event": "BarClosed",
    "symbol": "rb2510",
    "bar": { "O":3520, "H":3524, "L":3518, "C":3522, "V":156, "TickCount":47 },
    "elapsed": "0.3ms"
  }
  ↑ JSON Lines 格式，可 grep、可导入 PG、可被监控面板解析
```

### 3.1 Serge 的 Log 模型（使用 .NET ILogger + Serilog）

```csharp
// 使用 Serilog 的结构化日志能力
Log.Information(
    "Bar closed {Symbol} {Timestamp} O={Open} C={Close} V={Volume} Ticks={TickCount}",
    bar.Symbol, bar.Timestamp, bar.Open, bar.Close, bar.Volume, bar.TickCount);

// 自动生成 JSON:
// {"@t":"...","@m":"Bar closed rb2510 ...","Symbol":"rb2510","Open":3520,...}
```

### 3.2 日志分类 (Serilog SourceContext)

```
DataPipeline.Tick     — Tick 接收、写入 .tick 文件
DataPipeline.Bar      — Bar 聚合、闭合、写入 PG
DataPipeline.Import   — CSV/外部导入进度
Ctp.Connection        — CTP 连接/断线/重连
Ctp.MarketData        — CTP 行情订阅/取消
Strategy.Runtime      — 策略加载、启停、信号生成
Execution.Order       — 订单生成、提交、成交
Execution.Risk        — 风控检查结果
System.Health         — CPU/内存/磁盘/运行时长
System.Startup        — 启动序列
```

---

## 4. 运行时反馈 → WPF/Vue 监控面板

### 4.1 实时数据（SignalR 推送，不落日志文件）

```
┌──────────────────────────────────────────────────────┐
│  监控面板 (WPF / Vue)                                  │
├──────────────────────────────────────────────────────┤
│                                                      │
│  ┌──────────────────┐  ┌──────────────────┐          │
│  │ CTP 连接状态      │  │ 系统运行时长      │          │
│  │ ● 已连接 (3h)     │  │ 7d 12h 34m       │          │
│  └──────────────────┘  └──────────────────┘          │
│                                                      │
│  ┌──────────────────┐  ┌──────────────────┐          │
│  │ 今日 Tick 接收量   │  │ 今日 Bar 生成量   │          │
│  │ 1,234,567        │  │ 45,230 (98.2%)    │          │
│  └──────────────────┘  └──────────────────┘          │
│                                                      │
│  ┌──────────────────────────────────────────┐       │
│  │ 最近事件 (实时滚动)                        │       │
│  │ 09:01 Info  BarClosed rb2510 C=3522       │       │
│  │ 09:01 Info  BarClosed IF2606 C=3820       │       │
│  │ 08:55 Info  CTP MdApi Connected           │       │
│  │ 08:30 Warn  PG slow query: 3.2s           │       │
│  └──────────────────────────────────────────┘       │
│                                                      │
│  ┌──────────────────────────────────────────┐       │
│  │ 今日告警                                    │       │
│  │ 🟡 03:15 CTP 断连重连成功 (2次)              │       │
│  │ 🔴 无可紧急告警                               │       │
│  └──────────────────────────────────────────┘       │
│                                                      │
└──────────────────────────────────────────────────────┘
```

### 4.2 每日运行摘要（自动生成，推送到 Obsidian 知识库）

```markdown
# 2026-06-09 (周一) 运行日报

## 系统状态
- 运行时长: 24h (正常)
- CTP 连接: 断连 1 次 (15:00 结算正常断开), 重连 1 次 (21:00 夜盘恢复)
- 内存: 65 MB → 72 MB (正常, +7 MB)
- CPU: 平均 3.2%, 峰值 8.1%

## 数据采集
- Tick 接收: 2,345,678 条 (70 个品种)
- 1min Bar: 45,230 个
- Tick 文件: 70 个合约, 总计 188 MB
- 数据质量: 无 gap, TickCount 全部非零

## 交易
- 策略信号: 3 次
- 下单: 2 次 (1 次被风控拒绝: 保证金不足)
- 成交: 2 次 (rb2510 Buy 5@3520, Sell 5@3560)
- 今日盈亏: +2,000 元
- 手续费: 35.2 元

## 告警
- 🟡 15:00 CTP 正常断连
- 🔴 无严重告警
```

---

## 5. Log 存储与保留

| 日志类型 | 存储位置 | 保留期 | 格式 |
|---|---|---|---|
| 结构化运行日志 | `logs/tradingstudio-{yyyyMMdd}.jsonl` | 30 天 | JSON Lines |
| 每日报告 | `logs/reports/{yyyyMMdd}.md` → Obsidian 知识库 | 永久 | Markdown |
| 交易审计日志 | PostgreSQL `audit_log` 表 | 永久 | 结构化字段 |
| 错误堆栈 | `logs/errors/` | 永久 | 文本 |

### 5.1 文件滚动

```
logs/
├── tradingstudio-20260608.jsonl     ← 昨天的日志
├── tradingstudio-20260609.jsonl     ← 今天的日志（当前写入中）
├── reports/
│   ├── 20260608.md
│   └── 20260609.md                  ← 今日报告
└── errors/
    └── 20260608_210500_crash.log   ← 异常堆栈
```

---

## 6. 告警规则与触发

```
┌───────────┬────────────────────────────────────────┬──────────┐
│ 级别      │ 触发条件                                 │ 动作      │
├───────────┼────────────────────────────────────────┼──────────┤
│ 🔴 Critical│ 风控触发 (日损达上限)                    │ 电话+微信 │
│ 🔴 Critical│ 系统仓位 ≠ 实际仓位                      │ 电话+微信 │
│ 🔴 Critical│ 连续 5 次下单失败                        │ 电话+微信 │
│ 🔴 Critical│ 进程崩溃                                 │ 电话+微信 │
├───────────┼────────────────────────────────────────┼──────────┤
│ 🟡 Warning │ CTP 断连 > 5 分钟                       │ App推送  │
│ 🟡 Warning │ PG 写入失败                              │ App推送  │
│ 🟡 Warning │ Tick 接收量 < 预期的 50%                 │ App推送  │
│ 🟡 Warning │ 磁盘使用 > 80%                           │ App推送  │
│ 🟡 Warning │ 内存增长 > 10 MB/天                      │ App推送  │
├───────────┼────────────────────────────────────────┼──────────┤
│ 🟢 Info    │ 每日报告                                 │ 日报     │
│ 🟢 Info    │ 每周摘要                                 │ 周报     │
└───────────┴────────────────────────────────────────┴──────────┘
```

**手机通知实现**：

```
方案 (按优先级):
  1. Server酱 (免费, 微信推送) — 适合 🟡 Warning 和 🟢 Info
  2. Pushover (一次性付费 $5) — 支持优先级, 适合 🔴 Critical override
  3. 短信 (阿里云短信, 按条付费) — 兜底, 仅用于 Critical

推荐组合: Pushover (Critical) + Server酱 (Warning+Info)
原因: 个人项目, 低成本。$5 一次性费用 + 免费微信推送。
```

---

## 7. 健康检查端点

```csharp
// Minimal API: GET /health
// 返回 JSON, Vue 前端轮询, 也用于外部监控 (如 UptimeRobot)

app.MapGet("/health", () => new
{
    Status = "Healthy",           // Healthy | Degraded | Unhealthy
    Uptime = DateTime.UtcNow - ProcessStartTime,
    CtpMdConnected = true,
    CtpTdConnected = false,       // Phase 1 无交易
    PgConnected = true,
    MemoryMB = GC.GetTotalMemory(false) / 1024 / 1024,
    DiskFreeGB = drive.AvailableFreeSpace / 1024 / 1024 / 1024,
    TodayTicksReceived = 0,       // 实时更新
    TodayBarsGenerated = 0,
    LastError = null,
    PendingAlerts = 0
});
```

---

## 8. 与 TradingStudio 项目的集成

```
依赖: Serilog (NuGet)
  ├── Serilog.Sinks.File         → JSON Lines 文件
  ├── Serilog.Sinks.Console       → 开发时终端输出
  └── Serilog.Sinks.PostgreSQL    → 审计日志写入 PG

配置 (appsettings.json):
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "TradingStudio.Data": "Debug",       // 开发时更详细
        "TradingStudio.Ctp": "Information"
      }
    },
    "WriteTo": [
      { "Name": "File", "Args": { "path": "logs/tradingstudio-.jsonl", "rollingInterval": "Day" } },
      { "Name": "Console" }
    ]
  }
```

---

## 9. Phase 1 最小实现清单

```
□ Serilog 集成到 TradingStudio.App
□ JSON Lines 文件日志（按日滚动）
□ 关键事件写入 PG audit_log 表
□ /health 端点
□ 启动序列日志 (Info)
□ CTP 连接/断线日志 (Info + Warning)
□ Tick 接收量统计 (每 10 分钟记录一次 Info)
□ Bar 生成量统计 (每 10 分钟)
□ 错误日志 + 堆栈 (Error)
□ 每日自动生成报告 → Obsidian 知识库
```

> Phase 2+ 再补充：SignalR 推送、手机告警、WPF/Vue 监控面板集成。
