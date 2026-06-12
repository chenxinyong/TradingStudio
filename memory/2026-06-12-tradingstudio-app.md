---
name: 2026-06-12-tradingstudio-app
description: TradingStudio 主程序就位 — DI + Serilog + BackgroundService，行情采集命令完整可用
metadata:
  type: project
---

## 完成内容

### TradingStudio 可执行程序 (src/TradingStudio/)

标准 .NET Generic Host 架构：
- **Program.cs** — `Host.CreateApplicationBuilder` → Serilog + Options + HostedService
- **CollectService** — `BackgroundService`，7×24 行情采集（自动重连/重订阅/健康日志）
- **CollectOptions** — 强类型配置，从 `appsettings.json` 绑定
- **appsettings.json** — Serilog 全配置（控制台+文件）+ CTP 连接参数

### 配置

```json
{
  "Serilog": {
    "WriteTo": [
      { "Name": "Console", "Args": { "outputTemplate": "..." } },
      { "Name": "File", "Args": { "path": "log.txt", "rollingInterval": "Day" } }
    ]
  },
  "Collect": {
    "MdFront": "tcp://182.254.243.31:30011",
    "BrokerId": "9999",
    "UserId": "...",
    "Password": "..."
  }
}
```

### 运行

```bash
# 编译
dotnet build src/TradingStudio -c Debug

# 运行
src/TradingStudio/bin/Debug/net10.0/TradingStudio.exe
# Ctrl+C 退出
```

### 产出

- `bars.db` — SQLite (bars_1min + bars_day)
- `TickData/{交易所}/{合约}_{日期}.csv` — 金数源 42 列格式
- `log20260612.txt` — 按天滚动日志

### 架构

```
HostApplicationBuilder
├── Serilog (appsettings.json)
├── Options: CollectOptions
└── Services: CollectService (BackgroundService)
    ├── CTP MdApi → Quote
    ├── BarAggregator → Bar
    ├── DailyBarAggregator → Day Bar
    ├── BarStore → SQLite
    └── TickCsvWriter → CSV

生命周期:
  Connect → Login → Subscribe 928 contracts
  → 接收 Quote → Tick CSV + 1min Bar + Day Bar
  → 断连 → 指数退避重连 → 重订阅
  → 每分钟健康日志 + 日线刷新
  → Ctrl+C 优雅退出
```
