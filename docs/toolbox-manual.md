# TradingStudio.ToolBox 操作手册

> 版本: v1.0 | 日期: 2026-06-15

---

## 目录

1. [安装与部署](#1-安装与部署)
2. [命令速查](#2-命令速查)
3. [import — CSV 导入](#3-import--csv-导入)
4. [import-jinshuyuan — 金数源 RAR 导入](#4-import-jinshuyuan--金数源-rar-导入)
5. [import-url — URL 远程导入](#5-import-url--url-远程导入)
6. [verify — 数据验证](#6-verify--数据验证)
7. [典型工作流](#7-典型工作流)
8. [配置与日志](#8-配置与日志)
9. [常见问题](#9-常见问题)

---

## 1. 安装与部署

### 编译

```bash
dotnet publish src/TradingStudio.ToolBox/ -c Release -o dist/ToolBox/
```

### 部署产物

```
dist/ToolBox/
├── TradingStudio.ToolBox.exe    ← 入口
├── TradingStudio.Core.dll       ← 核心模型
├── TradingStudio.Data.dll       ← 数据层
├── appsettings.json             ← 配置
├── symbols.json                 ← 品种注册表 (75品种)
├── tools/UnRAR.exe              ← RAR 解压 (金数源所需)
└── *.dll                        ← 运行时依赖
```

### 运行

```bash
# Windows
TradingStudio.ToolBox.exe <command> [options]

# 开发模式
dotnet run --project src/TradingStudio.ToolBox -- <command> [options]
```

---

## 2. 命令速查

| 命令 | 别名 | 功能 | 输入 | 输出 |
|------|------|------|------|------|
| `import` | `i` | CSV Tick → Bar | 本地 CSV 文件/目录 | SQLite |
| `import-jinshuyuan` | `ij` | RAR → Bar | 金数源月度 RAR | SQLite |
| `import-url` | `iu` | URL → RAR → Bar | 远程 RAR URL | SQLite |
| `verify` | `v` | 数据质量检查 | SQLite 数据库 | 终端报告 + JSON |

**全局选项：**

```bash
ToolBox --help          # 列出所有命令
ToolBox <cmd> --help    # 显示命令帮助（待实现）
```

---

## 3. import — CSV 导入

### 功能

将金数源格式的 CSV Tick 文件聚合为 1 分钟 + 日线 Bar，写入 SQLite。

### 命令格式

```bash
ToolBox import --input <file|dir> [--db <path>]
ToolBox i -i <file|dir> [-d <db>]
```

### 参数

| 参数 | 短名 | 必需 | 默认值 | 说明 |
|------|------|------|--------|------|
| `--input` | `-i` | ✅ | — | CSV 文件路径，或包含 CSV 的目录（递归扫描） |
| `--db` | `-d` | ❌ | `bars.db` | SQLite 输出路径 |

### 示例

```bash
# 单个文件
ToolBox import -i "D:\Ticks\SHFE\rb2608_20260609.csv"

# 整个目录
ToolBox import -i "D:\Ticks\SHFE\" -d bars_shfe.db

# 短命令
ToolBox i -i tick.csv
```

### CSV 格式要求

- 金数源 44 列格式，GBK 编码
- 文件名格式: `{contract}_{YYYYMMDD}.csv`
- 示例: `rb2608_20260609.csv`

---

## 4. import-jinshuyuan — 金数源 RAR 导入

### 功能

从金数源月度 RAR 压缩包导入历史数据（1个月 = 1个 RAR，含 ~5,000-17,000 个 CSV）。

### 数据目录结构

```
{DataDir}/
├── FutAC_TickKZ_CTP_Daily_2020/
│   ├── FutAC_TickKZ_CTP_Daily_202001.rar
│   ├── FutAC_TickKZ_CTP_Daily_202002.rar
│   └── ...
├── FutAC_TickKZ_CTP_Daily_2021/
│   └── ...
└── FutAC_TickKZ_CTP_Daily_2026/
    └── ...
```

### 命令格式

```bash
ToolBox import-jinshuyuan --data-dir <dir> --layer <main|active|all> [options]
ToolBox ij -d <dir> -l <layer> [options]
```

### 参数

| 参数 | 短名 | 必需 | 默认值 | 说明 |
|------|------|------|--------|------|
| `--data-dir` | `-d` | ✅ | — | 金数源 RAR 根目录 |
| `--layer` | `-l` | ✅ | — | `main` / `active` / `all` |
| `--symbol` | `-s` | ❌ | 全部 | 品种代码（逗号分隔） |
| `--exchange` | `-e` | ❌ | — | 交易所（SHFE/DCE/CZCE/INE/GFEX） |
| `--from` | `-f` | ❌ | `202001` | 起始月份 `YYYYMM` |
| `--to` | `-t` | ❌ | `202512` | 结束月份 `YYYYMM` |
| `--db` | — | ❌ | `bars.db` | SQLite 路径 |
| `--temp-dir` | — | ❌ | 系统临时 | 解压目录 |
| `--dry-run` | — | ❌ | — | 不导入，仅列匹配 |

### Layer 详解

| Layer | 内容 | 适用场景 |
|-------|------|---------|
| `main` | 主力连续（`rb主力连续`） | 策略回测 —— 连续价格序列 |
| `active` | 活跃合约（当前交易月份） | 需要真实合约的成交量/持仓 |
| `all` | 全部合约（含已到期） | 全量数据积累、深度分析 |

### 示例

```bash
# 主力合约
ToolBox ij -d "D:\Jinshuyuan" -l main -f 202601 -t 202606

# 全合约 + 品种过滤
ToolBox ij -d "D:\Jinshuyuan" -l all -s rb,cu,ag -f 202001 -t 202012

# 交易所过滤
ToolBox ij -d "D:\Jinshuyuan" -l all -e SHFE

# 多个品种
ToolBox ij -d "D:\Jinshuyuan" -l all -s rb,cu,ag,au,zn,al

# 预览（不导入）
ToolBox ij -d "D:\Jinshuyuan" -l all --dry-run

# 自定义数据库和临时目录
ToolBox ij -d "D:\Jinshuyuan" -l all --db data/bars_shfe.db --temp-dir "E:\Temp"
```

### 数据量

| 容量 | 数值 |
|------|------|
| 每月 RAR | 5,000 ~ 17,000 CSV |
| 每月 Ticks | 5,000 万 ~ 1.7 亿 |
| 每月 Bars | 170 万 ~ 400 万 |
| DB 大小（月） | 2 ~ 5 GB |
| DB 大小（年） | 30 ~ 50 GB |
| 全年耗时 | 40 ~ 90 分钟 |

### 导入速度

按品种过滤可显著加速：

```bash
# 慢（全品种）: ~1.5h
ToolBox ij -d "D:\Jinshuyuan" -l all

# 快（单品种）: ~2min
ToolBox ij -d "D:\Jinshuyuan" -l all -s rb
```

---

## 5. import-url — URL 远程导入

### 功能

通过金数源 API 获取单日 RAR 下载地址，下载并导入。

### API

```bash
# 今日数据
curl "http://api.jinshuyuan.net/get_today_fileurl?apikey=157145f8520657fefcc45fe897153118&pdtnm=fut_tickkz_ctp_aftcls"
# → http://down.jinshuyuan.net/fut_tickkz_ctp_aftcls/20260615.rar?auth_key=...

# 指定日期
curl "http://api.jinshuyuan.net/get_his_fileurl?apikey=157145f8520657fefcc45fe897153118&pdtnm=fut_tickkz_ctp_aftcls&dt=20260601"
# → http://down.jinshuyuan.net/fut_tickkz_ctp_aftcls/20260601.rar?auth_key=...
```

### 命令格式

```bash
ToolBox import-url --url <url> [--date <YYYYMMDD>] [--db <path>] [--symbol <code>]
ToolBox iu -u <url> [-d <db>] [-s <code>]
```

### 参数

| 参数 | 短名 | 必需 | 默认值 | 说明 |
|------|------|------|--------|------|
| `--url` | `-u` | ✅ | — | RAR 下载 URL |
| `--date` | — | ❌ | 当天 | 交易日 `YYYYMMDD` |
| `--db` | `-d` | ❌ | `bars.db` | SQLite 路径 |
| `--symbol` | `-s` | ❌ | 全部 | 品种过滤 |

### 示例

```bash
# 一键下载今日数据
ToolBox iu -u "http://down.jinshuyuan.net/.../20260615.rar?auth_key=..." --date 20260615

# 指定数据库和品种
ToolBox iu -u "http://..." -d data/bars_live.db -s rb

# 脚本化（Linux/macOS）
URL=$(curl -s "http://api.jinshuyuan.net/get_today_fileurl?apikey=KEY&pdtnm=fut_tickkz_ctp_aftcls")
ToolBox iu -u "$URL" -d data/bars.db
```

### 流程

```
API 获取 URL → 下载 RAR (~118 MB) → UnRAR 解压 → CSV 筛选 → Bar 聚合 → SQLite → 清理临时文件
```

---

## 6. verify — 数据验证

### 功能

检查已导入数据库的 6 个维度数据质量。

### 命令格式

```bash
ToolBox verify [--db <path>] [--instrument <id>] [--from <date>] [--to <date>] [--output <json>]
ToolBox v [-d <db>] [-i <id>] [-f <date>] [-t <date>] [-o <json>]
```

### 参数

| 参数 | 短名 | 必需 | 默认值 | 说明 |
|------|------|------|--------|------|
| `--db` | `-d` | ❌ | `bars.db` | SQLite 路径 |
| `--instrument` | `-i` | ❌ | 全部 | 品种过滤 |
| `--from` | `-f` | ❌ | — | 起始日期 `YYYY-MM-DD` |
| `--to` | `-t` | ❌ | — | 结束日期 `YYYY-MM-DD` |
| `--output` | `-o` | ❌ | 终端 | JSON 报告输出路径 |

### 六维度

| 维度 | 检查项 | 严重度 |
|------|--------|--------|
| 完整性 | 交易日应有 Bar 缺失 | 🔴 |
| 一致性 | OHLC 逻辑矛盾（Low>High） | 🔴 |
| 唯一性 | 重复 `(instrument_id, bar_time)` | 🔴 |
| 异常值 | 成交量突增 > 均值×10 | 🟡 |
| 连续性 | 价格跳空 > 5% | 🟡 |
| 合规性 | 非交易时段数据（周末） | 🟡 |

### 示例

```bash
# 全库检查
ToolBox verify -d data/bars_2020.db

# 单品种
ToolBox v -d data/bars.db -i rb2608

# 日期范围 + 报告输出
ToolBox v -d data/bars.db -i rb2608 -f 2026-01-01 -t 2026-06-01 -o report.json
```

---

## 7. 典型工作流

### 7.1 首次导入历史数据

```bash
# 步骤 1: 导入 2020-2025 全品种
for year in 2020 2021 2022 2023 2024 2025; do
    ToolBox ij -d "D:\Jinshuyuan" -l all \
        -f ${year}01 -t ${year}12 \
        --db data/bars_${year}.db
done

# 步骤 2: 验证
for year in 2020 2021 2022 2023 2024 2025; do
    ToolBox v -d data/bars_${year}.db -o data/report_${year}.json
done

# 步骤 3: 检查报告
cat data/report_2020.json
```

### 7.2 只导入活跃交易的品种

```bash
# 导入 SHFE + DCE 活跃合约
ToolBox ij -d "D:\Jinshuyuan" -l active -e SHFE -f 202401 -t 202412
ToolBox ij -d "D:\Jinshuyuan" -l active -e DCE -f 202401 -t 202412
```

### 7.3 策略开发 —— 单品种专注

```bash
# 螺纹钢全合约 2024-2025
ToolBox ij -d "D:\Jinshuyuan" -l all -s rb -f 202401 -t 202512 --db data/rb.db

# 验证后回测
ToolBox v -d data/rb.db -i rb
TradingStudio backtest --config strategies/rb-ma.json --db data/rb.db
```

### 7.4 每日增量更新

```bash
#!/bin/bash
# daily_import.sh

API_KEY="157145f8520657fefcc45fe897153118"
DATE=$(date +%Y%m%d)
DB="data/bars_live.db"

# 获取今日 URL
URL=$(curl -s "http://api.jinshuyuan.net/get_today_fileurl?apikey=${API_KEY}&pdtnm=fut_tickkz_ctp_aftcls")

if [ -z "$URL" ]; then
    echo "Error: Failed to get download URL"
    exit 1
fi

# 下载导入
ToolBox iu -u "$URL" --date "$DATE" -d "$DB"

# 快速验证
ToolBox v -d "$DB" -i rb,cu,ag
```

### 7.5 补漏 —— 从 CSV 导入

```bash
# 如果已有解压好的 CSV 文件（非 RAR 格式）
ToolBox import -i "D:\Jinshuyuan\export\SHFE\" -d bars.db
```

---

## 8. 配置与日志

### appsettings.json

```json
{
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [
      { "Name": "Console" },
      { "Name": "File", "Args": { "path": "logs/toolbox-.log", "rollingInterval": "Day" } }
    ]
  },
  "Import": {
    "Database": "bars.db",
    "BatchSize": 1000
  },
  "Jinshuyuan": {
    "DataDir": "D:\\Jinshuyuan",
    "DefaultLayer": "main",
    "RarPassword": "www.jinshuyuan.net"
  }
}
```

### 日志位置

- 控制台：`[HH:mm:ss INF]` 格式
- 文件：`logs/toolbox-YYYYMMDD.log`（每日滚动，保留 30 天）

### 配置优先级

```
CLI 参数 > appsettings.json > 代码默认值
```

---

## 9. 常见问题

### Q: "UnRAR.exe not found"

```bash
# 确保 UnRAR.exe 在 tools/ 目录（与 ToolBox.exe 同级）
ls tools/UnRAR.exe
```

### Q: "No RAR files found in ..."

检查数据目录结构和文件名：
```
正确: {DataDir}/FutAC_TickKZ_CTP_Daily_2020/FutAC_TickKZ_CTP_Daily_202001.rar
错误: {DataDir}/2020/202001.rar
```

### Q: "No matching entries (of NNN)"

- 检查 `--layer` 参数：`main` 可能无匹配（某些品种无主力连续标记）
- 检查 `--symbol`：代码小写（`rb` 非 `RB`）
- 检查 `--from` / `--to`：月份格式为 `YYYYMM`

### Q: SQLite 锁冲突

```bash
# 导入时不要同时运行回测或采集
# SQLite 写锁是独占的
```

### Q: 速度慢

```bash
# 加速技巧：
# 1. 用 --symbol 减少处理量
# 2. --temp-dir 指向 SSD
# 3. --db 指向本地磁盘（不要网络路径）
```

### Q: C 盘空间不足

```bash
# 临时解压目录默认在 C 盘，大导入会占满
ToolBox ij -d "D:\Jinshuyuan" -l all --temp-dir "E:\Temp"
```
