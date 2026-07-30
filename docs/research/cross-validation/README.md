# 数据交叉验证

与文华财经/博易大师 K 线对比，验证 TradingStudio Bar 数据质量。

## 快速开始

### 1. 导出 TradingStudio 数据

```bash
# 导出 ag2612 5分钟 Bar
dotnet run --project src/TradingStudio.ToolBox -- bar-export \
    --db data/bars_history.duckdb \
    --instrument ag2612 \
    --table bars_5min \
    --from 2026-01-01 \
    --output data/crosscheck/ag2612_5min_ours.csv
```

### 2. 导出文华/博易参考数据

**文华财经 (赢顺云)**:
1. 打开品种 K 线图 → 选择对应周期（如 5 分钟）
2. 右键 → 数据导出 → Excel/CSV
3. 选择时间范围 → 导出
4. 另存为 UTF-8 CSV，命名为 `{品种}_wh.csv`

**博易大师**:
1. K 线图 → 数据管理 → 数据导出
2. 选择品种和时间范围 → 导出 CSV

### 3. 运行比对

```bash
python scripts/cross_validate.py \
    --ours data/crosscheck/ag2612_5min_ours.csv \
    --ref data/crosscheck/ag2612_5min_wh.csv \
    --instrument ag2612 \
    --tolerance 1 \
    --output data/crosscheck/ag2612_report.csv
```

### 4. 解读结果

| 评级 | 含义 | 行动 |
|------|------|------|
| **PASS** | OHLC 均 ≤ 1 tick | 数据质量可信 |
| **WARN** | 少量差异，需关注 | 检查具体差异 Bar，判断是系统差异还是 bug |
| **FAIL** | 大量异常 | 排查聚合逻辑 bug |

## 已知差异来源（非 bug）

1. **时间对齐**: 不同平台 Bar 开始时间可能差 ±1 分钟
2. **成交量**: 一些平台用单边计算，一些用双边；TradingStudio 用 delta 值
3. **交易日归属**: CTP 夜盘归属于下一交易日，文华可能归属于当天
4. **连续合约**: 换月规则不同（加权 vs 简单切换），连续合约数据不可直接对比

## 工具

| 工具 | 语言 | 用途 |
|------|------|------|
| `ToolBox bar-export` | C# | 从 DuckDB 导出 Bar CSV |
| `scripts/cross_validate.py` | Python | 双列 CSV 比对，输出报告 |
| `scripts/cross_validate_ag2612.py` | Python | 一键交叉验证（AKShare 参考数据） |

## 报告归档

| 日期 | 品种 | 周期 | 范围 | 结果 | 报告 |
|------|------|------|------|------|------|
| 2026-07-25 | ag2612 | 日线/5min | 07-17 ~ 07-24 | ⚠️ 数据稀疏，无法结论 | [ag2612-5min-2026-07.md](ag2612-5min-2026-07.md) |
