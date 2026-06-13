# TradingStudio 实盘部署

## 唯一的可执行文件

```
TradingStudio.exe                     ← 单 exe, 双模式
├── (无参数)    → 实盘引擎 (Windows Service + API localhost:5199 + SignalR)
├── backtest    → 回测 (一次性运行, 产出报告)
├── collect     → 行情采集 (Phase 1)
├── import      → CSV 导入 (Phase 1)
└── import-jinshuyuan → RAR 导入 (Phase 1)
```

## 部署目录

```
C:\TradingStudio\
├── TradingStudio.exe                  (单文件发布)
├── appsettings.json                   (CTP 连接 / 数据路径 / 风控参数)
├── symbols.json                       (品种数据, 75 品种)
├── configs\
│   └── strategies\
│       ├── ma-cross-rb.json           (策略 1: 双均线螺纹钢)
│       ├── bollinger-i.json           (策略 2: 布林带铁矿)
│       └── spread-rb-i.json           (策略 3: 跨品种套利)
├── data\
│   ├── bars.db                        (SQLite — Bar 数据)
│   └── ticks\                         (Tick CSV)
├── logs\
│   └── trading-20250613.log           (按日滚动, 保留 30 天)
├── health.json                        (健康监控, 每分钟刷新)
├── install.ps1
└── uninstall.ps1
```

## 一键安装

```powershell
# 管理员 PowerShell
.\deploy\install.ps1

# 自定义目录
.\deploy\install.ps1 -TargetDir D:\Trading

# 只更新策略配置, 重启服务
.\deploy\install.ps1 -ConfigOnly
```

`install.ps1` 做了什么:
1. `dotnet publish` TradingStudio 为单文件 → `C:\TradingStudio\`
2. 复制策略配置 + 建 data/logs 目录
3. 停止现有服务
4. `sc create TradingStudio binPath=... start=auto` 注册 Windows Service
5. 配置失败恢复: 10s → 30s → 60s 退避
6. 启动服务

## 日常管理

```powershell
# 服务状态
Get-Service TradingStudio

# 查看实时日志
Get-Content C:\TradingStudio\logs\trading-*.log -Tail 50 -Wait

# 重启 (策略配置修改后)
Restart-Service TradingStudio

# 临时停止
Stop-Service TradingStudio

# 卸载
.\deploy\uninstall.ps1
```

## 交易日生命周期

```
08:00  策略配置热加载 (AutoReload)
08:30  CTP 登录, 行情开始
  ↓
15:30  日盘收盘 — Flush Bar, 数据持久化
  ↓
20:30  CTP 重连, 夜盘行情
  ↓
03:00  夜盘收盘 — Flush Bar, 日终结算
  ↓
周末/节假日  休市等待, 自动恢复
```

## 进程清单

| 进程 | 启动方式 | 生命周期 |
|------|---------|---------|
| TradingStudio.exe | Windows Service (自动) | 7×24 |
| TradingStudio.UI.exe | 桌面快捷方式 (手动) | 按需 |

## 安全

- API 仅监听 localhost:5199
- CTP 凭证在 appsettings.json (Git 忽略)
- 风控规则只能收紧, 不能放宽
- 崩溃日志写入 crash.log
