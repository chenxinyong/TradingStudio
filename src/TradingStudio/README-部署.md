# TradingStudio Live 云端部署指南 (v0.5.0)

> 云端交易测试版。simnow 模拟盘（账号 058207，无真实资金）。
> 部署目标：Windows Server + 自包含发布 + CTP 直连 simnow 前置机。

---

## 1. 版本信息

| 项 | 值 |
|----|----|
| 版本 | **0.5.0** (2026-09-21) |
| 运行时 | .NET 10 自包含 win-x64（无需安装 .NET Runtime） |
| 交易环境 | **simnow 模拟盘**（`tcp://182.254.243.31:30001`，模拟资金） |
| 监控端口 | HTTP `:5001`（dashboard / API / SignalR） |
| 策略 | MaCross × 4：ag2612 / sa701 / TA701 / v2701（`strategies/live/`） |
| 预热库 | `data/bars_warmup.duckdb`（5.8 MB，4 品种历史 1min bar） |

## 2. 包内容

```
D:\TradingStudio\
├── TradingStudio.exe          引擎进程
├── appsettings.json           基础配置 (simnow 前置机/账号)
├── appsettings.cloud.json     云端覆盖 (WarmupDays=120, 预热库路径)
├── symbols.json               品种数据 (75 品种)
├── strategies\live\           4 个 MaCross 策略配置
├── data\
│   └── bars_warmup.duckdb     预热库 (只读, 启动时加载指标历史)
├── start-live.bat / .ps1      方式 B: 控制台看门狗 (双击启动)
├── install-service.ps1        方式 A: Windows 服务 (推荐)
└── README-部署.md             本文件
```

## 3. 前置条件（重要）

- **Windows Server 2019+ / 2022**（2C4G 足够）
- **时区必须是北京时间** —— SessionScheduler 按北京时段调度开盘/收盘：

```powershell
tzutil /s "China Standard Time"
```

- 出站 TCP 到 simnow 前置机 `182.254.243.31:30011/30001`（云厂商默认放行，无需配置）

## 4. 部署步骤

1. 解压到 `D:\TradingStudio\`（路径随意，不含中文/空格更稳）
2. 设置时区（见上）
3. 防火墙放行 5001（测试期全开，便于从家里访问 dashboard）：

```powershell
netsh advfirewall firewall add rule name="TradingStudio" dir=in action=allow protocol=TCP localport=5001
```

4. 选择启动方式：

### 方式 A：Windows 服务（推荐 — 开机自启 + 崩溃自愈）

```powershell
cd D:\TradingStudio
powershell -ExecutionPolicy Bypass -File .\install-service.ps1
```

服务注册为 `TradingStudio`（自动启动；崩溃后 5s/15s/60s 递增重启）。
卸载：`.\install-service.ps1 uninstall`

### 方式 B：控制台看门狗（调试观察用）

双击 `start-live.bat`（或 PowerShell 跑 `start-live.ps1`）。
引擎窗口 + 看门狗窗口两个控制台，看门狗每 10s 检查进程，死了自动拉起。

## 5. 验证清单（启动后逐项确认）

| # | 检查 | 命令 / 位置 | 通过标准 |
|---|------|------------|---------|
| 1 | HTTP 存活 | `curl http://localhost:5001/api/health` | 返回 JSON，Status=Running |
| 2 | Dashboard | 浏览器打开 `http://<服务器IP>:5001/dashboard` | 页面加载，5s 自动刷新 |
| 3 | CTP 行情登录 | `logs\live-YYYYMMDD.log` | `OnRspUserLogin` 成功，行情到达 |
| 4 | CTP 交易登录 | 同上 | `Trader` 登录成功（仅交易时段连接） |
| 5 | Bar 入库 | `data\bars_live.duckdb` 文件变大 | 盘中持续增长 |
| 6 | 健康文件 | `health.json` 每分钟刷新 | status/session 正确 |
| 7 | 预热加载 | 启动日志 | `Warmup` 相关日志无异常 |

## 6. 日常运维

```powershell
# 服务重启
powershell -ExecutionPolicy Bypass -File D:\TradingStudio\install-service.ps1 restart

# 看日志 (最新)
Get-Content D:\TradingStudio\logs\live-$(Get-Date -Format yyyyMMdd).log -Tail 50 -Wait

# 看资金/持仓
curl http://localhost:5001/api/portfolio
```

## 7. 安全注意

- **5001 无鉴权**，且有手动下单 / 暂停策略 / 热切换（平仓）接口。当前 simnow 测试期全开；**实盘前必须**用云安全组把 5001 限制为家庭 IP，或只绑 localhost。
- **simnow 账号单点登录**：同一账号在两处同时登录会互相踢线。启动云端之前，先停掉本机 TradingStudio（或任何其他 simnow 终端）。

## 8. 配置说明

配置分层（Program.cs 按 `DOTNET_ENVIRONMENT` 加载）：

```
appsettings.json          ← 基础配置（含 simnow 凭据）
appsettings.cloud.json    ← 云端覆盖（DOTNET_ENVIRONMENT=cloud 时生效）
```

- 启动脚本/服务已设置 `DOTNET_ENVIRONMENT=cloud`。
- 云端关键差异：`WarmupDays=120`（读预热库 240 天 1min bar）、`WarmupDatabase=bars_warmup.duckdb`。
- 换账号/换前置机（如 simnow 换实盘）：改 `appsettings.json` 的 `Live` 段，重启服务。
- 策略启停：编辑 `strategies\live\*.json` 后重启；或运行中通过 API 暂停/恢复单个策略。
