# ChanAnalysis — 缠论分析（C# 版）

用 C# 替代 `scripts/chan_*.py` 做缠论结构分析。一个工具，通用所有期货合约 + 股票指数。

## 用法

```bash
# 编译（一次即可）
dotnet build

# 期货（终端文本，周线+月线+日线）
dotnet run -- AG2609 白银
dotnet run -- V2701 PVC
dotnet run -- SA2701 纯碱

# 指数（终端文本，仅周线）
dotnet run -- index sz399317 国证A股

# Markdown 输出（供 run_chan_daily.py 追加到日志）
dotnet run -- md AG2612 白银       # 期货：周线+日线
dotnet run -- mdindex sz399317 国证A股   # 指数：仅周线
```

### 模式一览

| 模式 | 对象 | 级别 | 用途 |
|------|------|------|------|
| （默认） | 期货合约 | 月+周+日 | 终端查看 |
| `index` | 股票指数 | 仅周线 | 终端查看 |
| `md` | 期货合约 | 周+日 | Markdown，追加日志 |
| `mdindex` | 股票指数 | 仅周线 | Markdown，追加日志 |

### 关键参数 `min_gap`（笔的最小间隔）

- 期货：`min_gap=2`
- 指数：`min_gap=4`（指数波动小、分型密集，需更严格过滤）

在 `ReportGenerator.cs` 的 `BuildMarkdown` / `BuildReport` 里设置，`Program.cs` 里按 `isIndex` 自动选择。

## 每日自动分析

`scripts/run_chan_daily.py` 编排整个流程：

```bash
python scripts/run_chan_daily.py [YYYY-MM-DD]
```

- **指数**（`mdindex`）：国证A股 / 创业板 / 科创50 → 追加到 `01-Daily/股票/<date>.md`
- **持仓合约**（`md`）：见脚本顶部 `FUTURES` 配置 → 追加到 `01-Daily/期货/<date>.md`
- 幂等：已含缠论段则跳过；插入位置在「链接」段之前
- 持仓换月/平仓后，更新 `run_chan_daily.py` 顶部的 `FUTURES` 列表即可

## 输出内容

| 模块 | 说明 |
|------|------|
| 周线 / 日线（月线） | 各级别的笔、中枢、背驰、最近分型 |
| 走势形态 | 现价相对最近中枢：三买 / 三卖 / 中枢震荡 |
| 背驰 | 最后一笔与同向前一笔的幅度比值 |
| 均线 / 近期区间 / 持仓 | 终端模式下附加 |

## 核心算法

文件结构：

- `Models.cs` — Bar / Bi / Zhongshu 等数据结构
- `ChanEngine.cs` — 缠论核心：包含处理 → 分型 → 笔 → 中枢 → 背驰
- `TimeFrame.cs` — 日K线聚合为周线/月线（ISOWeek）
- `DataFetcher.cs` — 新浪期货日K线 + 股票指数日K线（含 JSONP 解析）
- `ReportGenerator.cs` — 终端报告 + Markdown 格式化
- `Program.cs` — 命令行入口（4 种模式）

## 与 Python 的对应关系

| Python | C# |
|--------|-----|
| `scripts/chan_ag2609.py` | `dotnet run -- AG2609 白银` |
| `scripts/chan_v2701.py` | `dotnet run -- V2701 PVC` |
| `scripts/chan_sa2701.py` | `dotnet run -- SA2701 纯碱` |
| `scripts/chan_indices2.py` | `dotnet run -- index sz399317 国证A股` |

> 数据说明：C# 直接抓新浪 API 最新数据，比旧 Python 脚本的本地快照更新（截至最近交易日）。
