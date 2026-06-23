# 期货品种深度研究计划 — Phase 3

> 基于 2020-2026 六年全量数据（80品种/5900万Bar），聚焦前30品种的深度研究。
> 分四阶段执行，总工期预估 8-12 个工作日。

---

## 一、数据全景（当前状态）

| 维度 | 现状 |
|------|------|
| Bar 数据 | 80 品种 × 6 年（2020-2025），1min + Day，SQLite 6 库共 ~9.5 GB |
| Tick 数据 | 当前仅保留当日（2026-06-18），55 品种，~542 文件 / 23.6 MB/日 |
| 品种规格 | symbols.json (75品种) + 知识库合约规格表 (82品种) |
| 历史Tick | 金数源 RAR 归档（2020-2025 每日收盘包），可按需回补 |
| 现有工具 | ToolBox: import / import-jinshuyuan / import-url / verify / merge / append / build-periods / analyze / continuous |
| 缺失 | CZCE Tick 目录未创建、无连续合约工具、无品种排名工具 |

### 品种代码映射（DB ↔ 标准代码）

DB 中部分 DCE 品种代码带后缀 `2`（如 `m2`→`m` 豆粕, `p2`→`p` 棕榈油, `i2`→`i` 铁矿石, `v2`→`v` PVC, `y2`→`y` 豆油, `c2`→`c` 玉米, `l2`→`l` 聚乙烯, `a2`→`a` 豆一, `b2`→`b` 豆二, `j2`→`j` 焦炭）。此为历史遗留问题，需在工具中统一映射。

---

## 二、TOP 30 品种排名（六年总成交量加权）

| 排名 | 代码 | 名称 | 交易所 | 分类 | 6年总成交量 | 合约周期 |
|------|------|------|--------|------|------------|----------|
| 1 | rb | 螺纹钢 | SHFE | 黑色金属 | 27.7亿 | 3主力 |
| 2 | TA | PTA | CZCE | 化工 | 21.7亿 | 3主力 |
| 3 | m | 豆粕 | DCE | 农产品 | 19.8亿 | 3主力 |
| 4 | MA | 甲醇 | CZCE | 化工 | 18.6亿 | 3主力 |
| 5 | SA | 纯碱 | CZCE | 化工 | 17.6亿 | 3主力 |
| 6 | FG | 玻璃 | CZCE | 化工 | 17.1亿 | 3主力 |
| 7 | fu | 燃料油 | SHFE | 能源 | 14.9亿 | 多月活跃 |
| 8 | ag | 白银 | SHFE | 贵金属 | 14.7亿 | 多月活跃 |
| 9 | p | 棕榈油 | DCE | 农产品 | 13.4亿 | 3主力 |
| 10 | v | PVC | DCE | 化工 | 12.0亿 | 3主力 |
| 11 | RM | 菜籽粕 | CZCE | 农产品 | 11.9亿 | 3主力 |
| 12 | i | 铁矿石 | DCE | 黑色金属 | 10.1亿 | 3主力 |
| 13 | y | 豆油 | DCE | 农产品 | 9.8亿 | 3主力 |
| 14 | c | 玉米 | DCE | 农产品 | 8.6亿 | 多月活跃 |
| 15 | hc | 热轧卷板 | SHFE | 黑色金属 | 8.5亿 | 3主力 |
| 16 | pp | 聚丙烯 | DCE | 化工 | 7.6亿 | 3主力 |
| 17 | OI | 菜籽油 | CZCE | 农产品 | 7.2亿 | 3主力 |
| 18 | bu | 石油沥青 | SHFE | 化工 | 6.7亿 | 多月活跃 |
| 19 | SR | 白糖 | CZCE | 农产品 | 6.3亿 | 3主力 |
| 20 | CF | 棉花 | CZCE | 农产品 | 6.2亿 | 3主力 |
| 21 | l | 聚乙烯 | DCE | 化工 | 5.8亿 | 3主力 |
| 22 | ru | 天然橡胶 | SHFE | 化工 | 5.6亿 | 3主力 |
| 23 | ni | 镍 | SHFE | 有色金属 | 5.3亿 | 多月活跃 |
| 24 | eg | 乙二醇 | DCE | 化工 | 5.1亿 | 3主力 |
| 25 | eb | 苯乙烯 | DCE | 化工 | 5.0亿 | 多月活跃 |
| 26 | sp | 纸浆 | SHFE | 化工 | 4.6亿 | 多月活跃 |
| 27 | al | 铝 | SHFE | 有色金属 | 4.2亿 | 多月活跃 |
| 28 | jm | 焦煤 | DCE | 黑色金属 | 4.1亿 | 3主力 |
| 29 | SM | 锰硅 | CZCE | 黑色金属 | 3.8亿 | 3主力 |
| 30 | zn | 锌 | SHFE | 有色金属 | 3.3亿 | 多月活跃 |

### 品类分布

| 大类 | 数量 | 品种 |
|------|------|------|
| 化工 | 11 | TA, MA, SA, FG, v, pp, bu, l, eg, eb, sp |
| 农产品 | 8 | m, p, RM, y, c, OI, SR, CF |
| 黑色金属 | 5 | rb, i, hc, jm, SM |
| 有色金属 | 4 | ni, al, zn, (cu 排39) |
| 能源 | 2 | fu, ru |
| 贵金属 | 1 | ag |

---

## 三、合约周期分类

### 分类标准

| 类型 | 判断标准 | 代表品种 |
|------|---------|----------|
| **三主力轮换** | Top 3 合约占比 > 70%，各有 > 10% | rb, TA, m, MA, SA, FG, p, v, RM, i, y, hc, pp, OI, SR, CF, l, ru, eg, jm, SM |
| **多月活跃** | Top 3 占比 < 65%，≥ 8 个合约有显著量 | ag, fu, ni, al, zn, au, cu, pb, sn, bu, eb, sp, si, lc, lu, sc, ss |
| **单合约主导** | 第一名 > 60% | 极少数新品种 |
| **少合约品种** | 全年 ≤ 6 个合约 | au, bz, lg, pt, pd (多为新上市或特殊品种) |

### TOP 30 合约周期明细

**三主力轮换型（主力月：01/05/09）** — 19 个：
rb, TA, m, MA, SA, FG, p, v, RM, i, y, hc, pp, OI, SR, CF, l, ru, jm, SM

这些品种的主力合约按"1月→5月→9月"年度周期轮换，每个主力合约活跃约 4 个月。

**多月活跃型** — 11 个：
fu, ag, c, bu, ni, eg, eb, sp, al, zn

这些品种要么是连续月份都有流动性（如有色金属 cu/al/ni/zn），要么是季节性品种（如 ag 每个偶数月合约交替活跃）。

---

## 四、执行阶段

### Phase 3a: 品种排名与分类工具（1-2天）

**目标**：构建可复用的品种排名和分类分析 CLI 工具。

```
ToolBox 新增命令：analyze
├── analyze rank        — 多维度品种排名（成交量/持仓量/成交额/波动率）
├── analyze classify    — 合约周期自动分类
└── analyze report      — 生成品种研究报告（Markdown）
```

**输入**：bars_2020~2025.db  
**输出**：
- `data/research/variety_ranking.csv` — 完整排名表
- `data/research/contract_cycle.csv` — 合约周期分类
- `docs/analysis/variety_report_YYYYMMDD.md` — 可读报告

**实现要点**：
- 支持多年度汇总排名和单年度排名
- 合约周期分类算法：Top3占比 + 有效合约数 + 月度分布熵
- DB 代码自动映射（m2→m 等）

---

### Phase 3b: 数据分层管理（2-3天）

**目标**：Top 30 保留完整 Tick+1min+Day；其余品种仅保留 1min+Day。

#### 3b-1: 历史 Tick 回补（Top 30）

利用金数源 RAR 归档（2020-2025 每日收盘包），回补 Top 30 品种的历史 Tick 数据。

```
ToolBox 新增命令：
├── tick-backfill       — 从金数源 RAR 回补指定品种 Tick
└── tick-prune          — 清理非 Top 30 的 Tick 数据
```

**数据量估算**：
| 项目 | 估算 |
|------|------|
| Top 30 每日 Tick | ~13 MB |
| Top 30 全年 Tick（250交易日） | ~3.2 GB |
| Top 30 六年 Tick（2020-2025） | ~19 GB |
| 当前可用磁盘 | 待确认 |

**存储结构**（重新设计）：
```
data/tick/
├── SHFE/
│   ├── rb/           ← 螺纹钢（Top 30，保留 Tick）
│   │   ├── 2020/
│   │   │   ├── rb2005_20200102.csv
│   │   │   └── ...
│   │   └── ...
│   ├── wr/           ← 线材（非 Top 30，无 Tick）
│   └── ...
├── DCE/
├── CZCE/
├── CFFEX/
├── INE/
└── GFEX/
```

#### 3b-2: 实时采集分流

修改 `CtpLiveFeed` / `LiveDataCollector`：
- 维护 Top 30 品种白名单
- Top 30 → Tick CSV + 1min Bar + Day Bar
- 其余品种 → 1min Bar + Day Bar（跳过 Tick CSV）

---

### Phase 3c: 品种加权连续合约（3-5天）

**目标**：为 Top 30 品种构建自定义加权连续合约，解决传统主力合约换月跳空问题。

#### 3c-1: 算法设计

**传统连续合约的问题**：
- 换月时产生跳空缺口
- 简单拼接丢失了远月合约的信息
- 无法反映合约间价差结构

**加权连续合约方案**（参考 Bloomberg / Wind 做法）：

```
加权价格 = Σ(合约i价格 × 权重i)
权重 = f(持仓量排名, 成交量排名, 距交割月天数)
```

**核心公式**：
```
For each day t:
  1. 选取该品种所有有成交的合约
  2. 计算每个合约的权重：
     w_i = normalize(α × volume_i + β × open_interest_i) × time_decay(months_to_delivery)
  3. 加权价格 = Σ(p_i × w_i)
  4. 加权成交量 = Σ(v_i × w_i)
  5. 加权持仓量 = Σ(oi_i × w_i)
```

**回溯调整**（Back-adjustment）：
- 以最新主力合约为锚
- 历史数据按换月价差比例调整（Panama / 比率回补）
- 可选：向前调整（forward-adjust）或向后调整（back-adjust）

#### 3c-2: 实现

```
ToolBox 新增命令：
├── continuous build   — 构建指定品种加权连续合约
├── continuous info    — 查看连续合约元信息
└── continuous export  — 导出连续合约 CSV
```

**输出数据**：
- `data/continuous/{品种}_continuous.db` — 每个品种一个 SQLite，含：
  - `continuous_1min` — 加权 1 分钟 K 线
  - `continuous_day` — 加权日线
  - `rollover_log` — 换月记录（日期、旧主力、新主力、价差、权重变化）

**加权参数**（可配置）：
```json
{
  "weighting": {
    "volume_alpha": 0.5,
    "open_interest_beta": 0.5,
    "time_decay": "linear",
    "min_weight": 0.05,
    "max_contracts": 5
  },
  "rollover": {
    "trigger": "volume_crossover",
    "confirmation_days": 3,
    "back_adjust": "ratio"
  }
}
```

#### 3c-3: 换月检测算法

```
1. 每日检查所有合约的成交量
2. 当"次主力合约"成交量连续 N 天 > "当前主力合约"成交量时，触发换月信号
3. 确认期（3天）后正式切换主力合约
4. 记录换月点：旧主力最后交易日、新主力第一交易日、价差、权重过渡窗口
```

#### 3c-4: 对比验证

构建的连续合约需与以下基准对比：
- 文华财经连续合约（市场公认标准）
- 简单主力拼接（只取持仓量最大的合约）
- 成交量加权拼接

---

### Phase 3d: 研究输出（2-3天）

#### 3d-1: 品种研究报告

每个 Top 30 品种生成一份标准化报告：
```
品种报告模板：
├── 基本信息（合约规格、交易时间、保证金、手续费）
├── 交易量分析（年度趋势、月度分布、主力合约活跃期）
├── 波动率特征（历史波动率、季节性波动模式）
├── 合约间价差结构（期限结构、升贴水模式）
├── 加权连续合约走势（2020-2026 OHLC 可视化数据）
└── 与其他品种的相关性矩阵
```

#### 3d-2: 可视化（为 WPF 客户端准备数据）

- 加权连续合约价格走势（6 年日线）
- 换月日历热力图
- 品种间相关性矩阵
- 成交量排名变迁动画数据

---

## 五、实施优先级

| 优先级 | 阶段 | 产出 | 可并行？ |
|--------|------|------|----------|
| **P0 立即** | 3a 品种排名分类 | 确定 Top 30 最终名单 + 分类 | — |
| **P1 高** | 3b-2 采集分流 | 节省日常 Tick 存储，只保留 Top 30 | 可与 3a 并行 |
| **P2 中** | 3c 连续合约 | 加权连续合约算法 + 数据 | 可与 3b-1 并行 |
| **P3 中** | 3b-1 历史回补 | Top 30 六年 Tick 回补 | 长耗时（需全量 RAR 扫描） |
| **P4 低** | 3d 研究报告 | 品种报告 + 可视化数据 | 依赖前面全部 |

---

## 六、技术决策（已确认，2026-06-18）

| # | 决策点 | 结论 | 理由 |
|---|--------|------|------|
| 1 | 连续合约加权方案 | **50/50 等权混合**（volume × 0.5 + oi × 0.5） | 简洁起步，后续根据回测效果调参 |
| 2 | 历史 Tick 回补范围 | **全六年回补**（2020-2025） | 约 19 GB，磁盘压力可控；全量数据支撑深度回测 |
| 3 | 回溯调整方式 | **比率法（ratio back-adjust）** | 价格翻倍后差值法误差大，比率法适合长期序列 |
| 4 | 非 Top 30 的 1min Bar | **保留** | 成本低（~10MB/品种/年），可作为对照研究和后续扩展 |
| 5 | 与现有 bars 的关系 | **独立并存**，不替代 | bars→回测（未调整价格），continuous→分析（连续价格序列），分工明确 |

> 📋 以上决策对应的实现任务已纳入 [TODO.md](TODO.md)，按优先级排入执行队列。

---

## 七、新增 ToolBox 命令总览

```
ToolBox 最终命令列表（Phase 3 完成后）：

已有命令：
  import              导入 Tick CSV → SQLite
  import-jinshuyuan   金数源 RAR → SQLite
  import-url          从 URL 导入
  verify              数据验证
  export              导出 CSV
  info                数据库信息

Phase 3 新增：
  analyze rank        品种多维度排名
  analyze classify    合约周期自动分类
  analyze report      生成品种研究报告
  tick-backfill       从金数源回补历史 Tick
  tick-prune          清理非重点品种 Tick
  continuous build    构建加权连续合约
  continuous info     查看连续合约信息
  continuous export   导出连续合约数据
```

---

## 八、文件与目录变更

```
新增目录：
  data/tick/                   ← 重新组织的 Tick 数据（按品种/年份）
  data/continuous/             ← 加权连续合约输出
  data/research/               ← 品种研究成果
  docs/analysis/               ← 品种分析报告（Markdown）
  src/TradingStudio.ToolBox/AnalyzeTool/    ← 品种分析命令
  src/TradingStudio.ToolBox/ContinuousTool/  ← 连续合约命令
  src/TradingStudio.ToolBox/TickTool/        ← Tick 管理命令

代码变更：
  src/TradingStudio/Live/CtpLiveFeed.cs        ← 采集分流
  src/TradingStudio/Services/LiveDataCollector.cs  ← Tick 写入策略
  src/TradingStudio.ToolBox/ToolRegistry.cs    ← 注册新命令
  src/TradingStudio/symbols.json              ← 新增 Top30 标记字段
```

---

> 下一步：确认技术决策点（第六节 5 个问题）后，按优先级推进实施。
