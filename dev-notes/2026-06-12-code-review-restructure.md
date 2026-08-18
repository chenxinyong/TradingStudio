---
name: 2026-06-12-code-review-restructure
description: 代码结构审核重组 + 交易时段调度 + 健康监控 + DI/Serilog + 文档整理
metadata:
  type: project
---

## 完成内容

### 1. 代码结构重组（对照文档）

| 调整 | 说明 |
|------|------|
| `TradingStudio.Data` 新建 | Aggregation + Storage 从 Core 移出 |
| `TradingStudio.Core` 精简 | 仅保留 Models (Exchange, Future, TickRecord, Bar) |
| `TradingStudio` 分层 | Services/ (CollectService, SessionScheduler, HealthMonitor) + Options/ (CollectOptions) |
| `symbols.json` 去重 | 唯一位置: src/TradingStudio/symbols.json |

### 2. 交易时段调度 (SessionScheduler)

- 日盘 08:30-15:30 自动连接，夜盘 20:30-03:00 自动连接
- 周末/节假日(26个)自动休市，WaitUntilNextSession()
- SessionName() 返回"日盘"/"夜盘"/"休市"

### 3. 健康监控 (HealthMonitor)

- health.json 每60秒刷新：status/session/quotes/bars/reconnects/uptime
- 外部脚本可轮询监控

### 4. DI + Serilog

- Program.cs 8行：Host.CreateApplicationBuilder → Serilog + Options + HostedService
- appsettings.json 全配置：Serilog(控制台+文件按天滚动) + Collect(Ctp连接+路径)
- 日志方法：Log.Information()/Warning()/Error()
- 控制台: HH:mm:ss [3字母级别]，文件: 完整时间戳

### 5. 文档整理

- docs/contracts/ 从git恢复76个品种md + CSV
- CLAUDE.md 更新：项目结构、路由表、阶段完成状态
- 根 CLAUDE.md 更新：CTP封装位置

### 最终项目结构

```
src/
├── CTP/ {SDK/, Wrapper/}
├── TradingStudio.Core/Models/
├── TradingStudio.Data/ {Aggregation/, Storage/}
├── TradingStudio.Ctp/
├── TradingStudio/ {Services/, Options/, appsettings.json, symbols.json}
└── Scripts/
```
