# 数据导入操作手册

> TradingStudio.ToolBox 历史数据导入指南

---

## 命令总览

| 命令 | 别名 | 功能 |
|------|------|------|
| `import` | `i` | 本地 CSV Tick → Bar → SQLite |
| `import-jinshuyuan` | `ij` | 本地金数源 RAR → Bar → SQLite |
| `import-url` | `iu` | 远程 URL 下载 RAR → Bar → SQLite |
| `verify` | `v` | 数据质量检查（6 维度） |

---

## 1. CSV 导入 (`import`)

将金数源格式的 CSV Tick 文件聚合为 1min + Day Bar，写入 SQLite。

### 用法

```bash
# 单个文件
ToolBox import -i "D:\Ticks\SHFE\rb2608_20260609.csv"

# 整个目录（递归扫描）
ToolBox import -i "D:\Jinshuyuan\export\SHFE\" -d bars.db

# 指定编码
ToolBox i -i tick.csv -d custom.db -e utf-8
```

### 参数

| 参数 | 短名 | 必需 | 默认值 | 说明 |
|------|------|------|--------|------|
| `--input` | `-i` | ✅ | — | CSV 文件或目录路径 |
| `--db` | `-d` | ❌ | `bars.db` | SQLite 数据库路径 |

---

## 2. 金数源 RAR 导入 (`import-jinshuyuan`)

从金数源 RAR 压缩包（月度归档）导入历史数据。

### 前置条件

- 数据目录结构：`{DataDir}/FutAC_TickKZ_CTP_Daily_{YYYY}/FutAC_TickKZ_CTP_Daily_{YYYYMM}.rar`
- UnRAR.exe 位于 `tools/` 目录
- `symbols.json` 位于 exe 同级目录

### 用法

```bash
# 导入 rb 全合约 2020 全年
ToolBox ij -d "D:\Jinshuyuan" -l all -s rb -f 202001 -t 202012

# 导入主力合约 rb,cu,ag 2026上半年
ToolBox ij -d "D:\Jinshuyuan" -l main -s rb,cu,ag -f 202601 -t 202606

# 导入 SHFE 全品种
ToolBox ij -d "D:\Jinshuyuan" -l all -e SHFE

# 预览匹配文件（不导入）
ToolBox ij -d "D:\Jinshuyuan" -l all -s rb --dry-run

# 指定临时目录（避免 C 盘空间不足）
ToolBox ij -d "D:\Jinshuyuan" -l all --temp-dir "E:\Temp"
```

### 参数

| 参数 | 短名 | 必需 | 默认值 | 说明 |
|------|------|------|--------|------|
| `--data-dir` | `-d` | ✅ | — | 金数源 RAR 数据根目录 |
| `--layer` | `-l` | ✅ | — | `main`(主力) / `active`(活跃) / `all`(全合约) |
| `--symbol` | `-s` | ❌ | 全部 | 品种代码（逗号分隔，如 `rb,cu,ag`） |
| `--exchange` | `-e` | ❌ | — | 交易所过滤（SHFE/DCE/CZCE/INE/GFEX） |
| `--from` | `-f` | ❌ | `202001` | 起始月份（YYYYMM） |
| `--to` | `-t` | ❌ | `202512` | 结束月份（YYYYMM） |
| `--db` | — | ❌ | `bars.db` | SQLite 数据库路径 |
| `--temp-dir` | — | ❌ | 系统临时目录 | 解压临时目录 |
| `--dry-run` | — | ❌ | — | 仅列出匹配文件，不导入 |

### Layer 说明

| Layer | 导入内容 | 适用场景 |
|-------|---------|---------|
| `main` | 主力连续合约（如 `rb主力连续`） | 策略回测——只需要连续价格序列 |
| `active` | 活跃交易合约（非连续、品种在 symbols.json 中） | 需要具体合约的成交量/持仓量 |
| `all` | 所有合约（含已到期、不活跃） | 全量数据积累 |

### 数据量参考

| 年份 | RAR 数 | Ticks | Bars | 耗时 |
|------|--------|-------|------|------|
| 2020 | 12 | 12.5 亿 | 3,393 万 | 1h17m |
| 每个 RAR | 1 | 5,000~17,000 CSV | 200~400 万 Bar | 5~8 分钟 |

---

## 3. URL 远程导入 (`import-url`)

通过金数源 API 下载指定交易日的 RAR 并导入。

### API 获取下载 URL

```bash
# 获取今日数据下载地址
curl "http://api.jinshuyuan.net/get_today_fileurl?apikey=157145f8520657fefcc45fe897153118&pdtnm=fut_tickkz_ctp_aftcls"
# 返回: http://down.jinshuyuan.net/fut_tickkz_ctp_aftcls/20260615.rar?auth_key=...

# 获取指定日期数据
curl "http://api.jinshuyuan.net/get_his_fileurl?apikey=157145f8520657fefcc45fe897153118&pdtnm=fut_tickkz_ctp_aftcls&dt=20260601"
```

### 用法

```bash
# 下载并导入今日数据
ToolBox iu --url "http://down.jinshuyuan.net/fut_tickkz_ctp_aftcls/20260615.rar?auth_key=..." --date 20260615

# 指定品种过滤和数据库
ToolBox iu -u "http://..." -d bars_live.db -s rb --date 20260615
```

### 参数

| 参数 | 短名 | 必需 | 默认值 | 说明 |
|------|------|------|--------|------|
| `--url` | `-u` | ✅ | — | RAR 下载 URL |
| `--date` | — | ❌ | 当天 | 交易日（YYYYMMDD） |
| `--db` | `-d` | ❌ | `bars.db` | SQLite 数据库路径 |
| `--symbol` | `-s` | ❌ | 全部 | 品种过滤 |

---

## 4. 数据验证 (`verify`)

检查已导入数据库的数据质量。

### 用法

```bash
# 检查全部数据
ToolBox verify -d bars.db

# 检查特定品种
ToolBox v -d bars.db -i rb2608

# 指定日期范围
ToolBox v -d bars.db -i rb2608 -f 2026-01-01 -t 2026-06-01

# 输出 JSON 报告
ToolBox v -d bars.db -o report.json
```

### 六维度检查

| 维度 | 检查项 |
|------|--------|
| 完整性 | 交易日应有 Bar 是否缺失 |
| 一致性 | OHLC 逻辑矛盾 (Low>High 等) |
| 唯一性 | 同一 (instrument_id, bar_time) 重复行 |
| 异常值 | 单分钟成交量 > 均值×10 |
| 连续性 | 相邻 Bar 价格跳空 > 5% |
| 合规性 | Bar 时间落在周末/非交易时段 |

---

## 5. 典型工作流

### 场景 A：首次导入全年数据

```bash
# 1. 导入 2020-2025 全部主力合约
for year in 2020 2021 2022 2023 2024 2025; do
    ToolBox ij -d "D:\Jinshuyuan" -l main \
        -f ${year}01 -t ${year}12 \
        --db data/bars_${year}.db
done

# 2. 验证数据质量
ToolBox v -d data/bars_2020.db

# 3. 查看数据库信息（未来功能）
# ToolBox info -d data/bars_2020.db
```

### 场景 B：每日增量更新

```bash
# 1. 获取今日下载地址
URL=$(curl -s "http://api.jinshuyuan.net/get_today_fileurl?apikey=KEY&pdtnm=fut_tickkz_ctp_aftcls")

# 2. 下载并导入
ToolBox iu --url "$URL" --date $(date +%Y%m%d) -d data/bars_live.db

# 3. 快速验证
ToolBox v -d data/bars_live.db
```

### 场景 C：导入单品种用于回测

```bash
# 1. 导入 rb 2024-2025 主力合约
ToolBox ij -d "D:\Jinshuyuan" -l all -s rb -f 202401 -t 202512 --db data/rb_bars.db

# 2. 验证
ToolBox v -d data/rb_bars.db -i rb

# 3. 回测
TradingStudio backtest --config strategies/rb-ma-cross.json --db data/rb_bars.db
```

---

## 6. 目录约定

```
D:\TradingStudio\
├── ToolBox\
│   ├── TradingStudio.ToolBox.exe
│   ├── tools\UnRAR.exe           ← 解压工具
│   ├── appsettings.json
│   └── symbols.json               ← 品种注册表
│
├── data\
│   ├── bars.db                    ← 主数据库
│   ├── bars_2020.db               ← 按年分库
│   └── bars_live.db               ← 实时增量
│
└── Jinshuyuan\                    ← 金数源 RAR 数据
    ├── FutAC_TickKZ_CTP_Daily_2020\
    │   ├── FutAC_TickKZ_CTP_Daily_202001.rar
    │   └── ...
    └── FutAC_TickKZ_CTP_Daily_2026\
        └── ...
```

## 7. 常见问题

**Q: "UnRAR.exe not found"**
将 UnRAR.exe 复制到 `tools/` 目录（与 ToolBox.exe 同级）。

**Q: "No RAR files found"**
检查数据目录路径和目录结构。RAR 文件应位于 `{DataDir}/FutAC_TickKZ_CTP_Daily_{YYYY}/` 下。

**Q: 导入速度慢**
- 使用 SSD 存放临时目录（`--temp-dir` 指定）
- 使用 `--symbol` 过滤，减少处理量
- 数据库放在本地磁盘（不要网络路径）

**Q: SQLite 锁冲突**
导入时不要同时运行回测或采集（SQLite 写锁是独占的）。
