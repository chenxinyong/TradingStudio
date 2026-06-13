# TradingStudio 数据导入指南

> 支持两种来源：金数源 CSV 文件 和 金数源 RAR 历史数据库。
> 统一输出：SQLite Bar 数据库（bars_1min + bars_day）。

---

## 快速开始

```bash
# 单文件 CSV 导入
TradingStudio import --input cu1603_20160111.csv --db bars.db

# 目录批量 CSV 导入
TradingStudio import --input ./extracted_csv/ --db bars.db

# 金数源 RAR 流式导入（不落盘）
TradingStudio import-jinshuyuan --layer all --symbol cu --db copper.db
```

---

## 命令一：`import` — CSV 文件导入

### 用法

```
TradingStudio import --input <file|dir> [--db bars.db]
```

### 参数

| 参数 | 必需 | 默认值 | 说明 |
|------|------|--------|------|
| `--input` | ✅ | — | CSV 文件路径 或 目录路径 |
| `--db` | | `bars.db` | 输出 SQLite 路径 |

### 示例

```bash
# 单文件
TradingStudio import --input data\ticks\金数源_cu1603_20160111_CTP格式.csv

# 目录批量（支持 金数源_*_CTP格式.csv 命名模式）
TradingStudio import --input data\ticks\

# 指定输出库
TradingStudio import --input data\ticks\ --db silver_2021.db
```

### 文件格式要求

- **编码**：GBK（code page 936）
- **分隔符**：逗号（`,`）
- **列数**：44 列（CTP 全字段格式）
- **表头**：必须有中文表头行
- **文件名**：`{描述}_{合约}_{交易日}_CTP格式.csv`

---

## 命令二：`import-jinshuyuan` — RAR 历史数据库导入

### 用法

```
TradingStudio import-jinshuyuan --layer <main|active|all> [options]
```

### 参数

| 参数 | 必需 | 默认值 | 说明 |
|------|------|--------|------|
| `--layer` | ✅ | — | `main`=主力连续 / `active`=已知品种 / `all`=全量 |
| `--symbol` | | — | 品种过滤，可重复。例：`--symbol ag --symbol cu` |
| `--exchange` | | — | 交易所过滤：`SHFE` `DCE` `CZCE` `INE` `GFEX` |
| `--from` | | `202101` | 起始月份 YYYYMM |
| `--to` | | `202212` | 结束月份 YYYYMM |
| `--data-dir` | | `C:\Works\Datas\Jinshuyuan` | 金数源根目录 |
| `--db` | | `bars_history.db` | 输出 SQLite 路径 |
| `--dry-run` | | — | 只列匹配文件数，不实际导入 |

### 三个 Layer 的含义

| Layer | 匹配规则 | 用途 |
|-------|---------|------|
| `main` | 合约名含"主力连续" | 策略回测（连续价格序列） |
| `active` | 品种在 symbols.json 中的单体合约 | 活跃品种研究 |
| `all` | 所有 CSV 文件 | 全量导入，通常配合 `--symbol` |

> ⚠️ 当前金数源数据中**未发现主力连续合约文件**。`--layer main` 匹配数为 0。
> 实际使用推荐 `--layer all --symbol <品种>` 导入指定品种的全部合约。

### 示例

```bash
# 1. 预览导入量（不写库）
TradingStudio import-jinshuyuan --layer all --symbol ag --dry-run

# 2. 导入白银全部合约（24 个月）
TradingStudio import-jinshuyuan --layer all --symbol ag --db silver.db

# 3. 只导入上期所 2021 年数据
TradingStudio import-jinshuyuan --layer all --exchange SHFE --from 202101 --to 202112

# 4. 导入铜+铝+锌
TradingStudio import-jinshuyuan --layer all --symbol cu --symbol al --symbol zn

# 5. 全量导入（376K 文件，预计数天）
TradingStudio import-jinshuyuan --layer all
```

### 导入性能

| 场景 | 数据量 | 耗时 |
|------|--------|------|
| 单品种 1 个月（大豆"a"） | 120 合约 × ~1.2M ticks | ~40 秒 |
| 单品种 24 个月 | ~2,900 合约 × ~30M ticks | ~15 分钟 |
| 全品种 24 个月（--layer all） | 376K 合约 × ~3B ticks | 数小时~数天 |

---

## 数据源

### 金数源（www.jinshuyuan.net）

- **路径**：`C:\Works\Datas\Jinshuyuan\`
- **结构**：`FutAC_TickKZ_CTP_Daily_{YYYY}\FutAC_TickKZ_CTP_Daily_{YYYYMM}.rar`
- **覆盖**：2021-01 → 2022-12，5 家交易所，64 品种
- **规模**：24 个 RAR，57 GB 压缩 → 1.5 TB 解压，376,485 个 CSV
- **密码**：`www.jinshuyuan.net`

### RAR 内部结构

```
{YYYYMM}.rar
├── sc/{contract}_{YYYYMMDD}.csv    # SHFE 上期所
├── dc/{contract}_{YYYYMMDD}.csv    # DCE 大商所
├── zc/{contract}_{YYYYMMDD}.csv    # CZCE 郑商所
├── ine/{contract}_{YYYYMMDD}.csv   # INE 上能源
└── gfex/{contract}_{YYYYMMDD}.csv  # GFEX 广期所
```

### 交易所映射

| RAR 目录 | 交易所 | CLI 参数 |
|----------|--------|----------|
| `sc/` | 上海期货交易所 | `SHFE` |
| `dc/` | 大连商品交易所 | `DCE` |
| `zc/` | 郑州商品交易所 | `CZCE` |
| `ine/` | 上海国际能源交易中心 | `INE` |
| `gfex/` | 广州期货交易所 | `GFEX` |

### CSV 格式

- 44 列 CTP 全字段（与 CTP 实时行情输出一致）
- GBK 编码
- 价格字段：`double` × F4 格式（如 `35630.0000`）
- 成交量 / 成交额：**累计值**（导入时自动计算 delta）

---

## 输出数据库

### 表结构

```sql
-- 1分钟K线
CREATE TABLE bars_1min (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    instrument_id TEXT    NOT NULL,   -- 合约代码: "cu2501", "a2101"
    trading_day   TEXT    NOT NULL,   -- 交易日: "2021-01-04"
    bar_time      TEXT    NOT NULL,   -- Bar时间: "2021-01-04 09:01:00"
    open          INTEGER NOT NULL,   -- 开盘价 × 10^7
    high          INTEGER NOT NULL,   -- 最高价 × 10^7
    low           INTEGER NOT NULL,   -- 最低价 × 10^7
    close         INTEGER NOT NULL,   -- 收盘价 × 10^7
    volume        INTEGER NOT NULL,   -- 成交量（delta）
    turnover      REAL    NOT NULL,   -- 成交额（delta）
    open_interest REAL    NOT NULL,   -- 持仓量（快照）
    tick_count    INTEGER DEFAULT 0,  -- 构成此Bar的tick数
    UNIQUE(instrument_id, bar_time)
);

-- 日线
CREATE TABLE bars_day (
    -- 同 bars_1min
    UNIQUE(instrument_id, bar_time)
);
```

### 价格换算

所有价格存储为 `long` 整数，精度因子 `PriceScale = 10,000,000`（10^7）。

```csharp
// 存储: (double)price × 10,000,000 → (long)
// 读取: (long)price / 10,000,000 → (double)
long stored = (long)(35630.0000 * 10_000_000);  // 356,300,000,000
double price = stored / 10_000_000.0;            // 35630.0
```

### 跨月去重

`UNIQUE(instrument_id, bar_time)` 约束 + `INSERT OR REPLACE` 确保同一合约同一分钟的 Bar 不重复。多次导入同一个月的数据安全。

---

## 导入流程图

```
┌─────────────────────────────────────────────────────────┐
│  import-jinshuyuan --layer all --symbol ag               │
├─────────────────────────────────────────────────────────┤
│                                                          │
│  1. 扫描目录                                              │
│     C:\Works\Datas\Jinshuyuan\                           │
│     ├── FutAC_TickKZ_CTP_Daily_2021\                     │
│     │   ├── ...202101.rar  ← 匹配                        │
│     │   ├── ...202102.rar  ← 匹配                        │
│     │   └── ...                                           │
│     └── FutAC_TickKZ_CTP_Daily_2022\                     │
│                                                          │
│  2. 逐 RAR 处理                                          │
│     ┌──────────────────────────────────────┐             │
│     │ unrar lb archive.rar -pPASSWORD       │             │
│     │   → 列出 15,217 条目                   │             │
│     │   → JinshuyuanEntryFilter 过滤          │             │
│     │   → 匹配 N 个条目                      │             │
│     │                                        │             │
│     │ foreach 匹配条目:                       │             │
│     │   unrar p archive.rar -pPASS -inul     │             │
│     │     → stdout (GBK CSV stream)           │             │
│     │     → CsvTickImporter.Parse(stream)     │             │
│     │     → TickRecord                       │             │
│     │     → BarAggregator.Feed()             │             │
│     │     → DailyBarAggregator.Feed()        │             │
│     │                                        │             │
│     │ Flush → List<Bar>                      │             │
│     │ BarStore.WriteBatchAsync → SQLite       │             │
│     └──────────────────────────────────────┘             │
│                                                          │
│  3. 输出: bars_history.db                                │
│     ├── bars_1min (60M rows)                             │
│     └── bars_day  (2K rows)                              │
└─────────────────────────────────────────────────────────┘
```

---

## 依赖

| 组件 | 用途 |
|------|------|
| `UnRAR.exe` v7.22 | RAR 解压（命令行子进程） |
| `System.Text.Encoding.CodePages` | GBK 编码支持 |
| `Microsoft.Data.Sqlite` | SQLite 存储 |

UnRAR.exe 查找顺序：
1. 项目 `tools/` 目录（随项目分发）
2. 可执行文件同目录
3. 系统 PATH

---

## 代码文件

```
src/TradingStudio.Data/Import/
├── CsvTickImporter.cs           GBK CSV → TickRecord 解析器
├── TickImportService.cs         CSV 文件 → Bar 编排
├── JinshuyuanEntryFilter.cs     RAR 条目路径解析 + Layer/Symbol/Exchange 过滤
├── JinshuyuanOptions.cs         配置 record
└── JinshuyuanImportService.cs   UnRAR.exe 子进程 → 流式导入编排

src/TradingStudio/Program.cs     CLI 入口 (import + import-jinshuyuan)
src/TradingStudio/tools/
└── UnRAR.exe                    RAR 解压工具
```
