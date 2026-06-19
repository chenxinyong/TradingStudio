# TradingStudio 待办清单

> 整理自 6/14–6/16 三日计划 + Phase 3 品种深度研究。
> 最后更新：2026-06-19

---

## 🔴 P0 — 阻塞项

### 数据准备：碎片整理为可回测状态（2026-06-18 新增）

> 详细方案：[数据准备方案](../analysis/数据准备方案.md)
> 核心问题：6 个 per-year DB 存在但回测引擎只打开一个文件 → 无法跑跨年回测

- [x] **合并 6 年 DB → `bars_merged.duckdb`** ✅ 2026-06-18
  - DuckDB 5.0 GB（SQLite 9.1 GB → 压缩 45%），5926 万行，0 数据异常
  - 4 索引（inst+time / trading_day），查询 < 50ms
  - 新增命令：`ToolBox merge`（alias: `m`）
- [ ] **合约代码映射：产品代码 → 具体合约**
  - 策略配置写 `"ag"`，DB 存 `"ag2608"`，需 `ContractCodeGenerator` 展开
  - 在 `HistoricalBarFeed` 或策略初始化层做映射
- [ ] **Tick CSV 迁移** — 542 文件从 `src/TradingStudio/TickData/` → `data/ticks/`
- [x] **端到端回测验证** — SA 30min 缠论策略跑通 ✅ 2026-06-19
  - 修复：预热隔离 + 时间驱动BI匹配 + 防重复入场
  - 验证：2025-06~08, Trades=13, Return=+18.13%, MaxDD=1.96%

### Simnow 实盘验证（挂了三天 ⚠️）

- [x] 注册 simnow 账号 ✅ 13961193449 (已注册)
- [x] 配置 `appsettings.local.json` ✅ 2026-06-19 (密码修正 + TraderFront/AuthCode/MdFront 补全)
- [ ] 启动 `TradingStudio live` 连线验证 ⚠️ 端午节休市 (6/19), 待交易日
  - TraderApi TLS: 0x1001 错误待排查 (TLS true/false?)
  - MdApi: EngineHost 在 SessionScheduler 等待交易时段开始
- [ ] CTP TraderApi 下单字段映射（待 C++/CLI 封准确认）

> 6/14 列 → 6/15 列 → 6/16 列 → 6/19 端午节阻塞。
> 密码修正 + 配置补全完成, 待交易日连线验证。
> 回测引擎再漂亮，不跑一次实盘链路不知道 TraderApi 封装有没有坑。

### Phase 2 回测 — 端到端验证

- [ ] Phase 2a 最小回测范围确定（单品种/单策略/日线）
- [ ] 多策略端到端验证（SubPortfolio 框架已有，没跑过）

### Phase 2b 回测精度优化（2026-06-18 新增，基于 Lean 对比分析）

> 详细分析：[TradingStudio vs Lean 对比分析](../analysis/TradingStudio-vs-Lean-对比分析.md)
> 实施路线：[Phase 2b 优化路线图](../analysis/Phase2b-优化路线图.md)

#### 🔴 P0 — 阻塞策略研究

- [ ] **限价单/止损单成交价修正**（[ExecutionHandler.cs](../../src/TradingStudio.Engine/ExecutionHandler.cs#L289)）
  - 限价买单：Low → min(limitPrice, High)，不应以最低价成交
  - 限价卖单：High → max(limitPrice, Low)，不应以最高价成交
  - 止损单：触发后以 stopPrice/Open 成交，而非极端价
  - 验证：修正后限价策略 Sharpe 应下降 5-15%
- [ ] **平今/平昨手续费区分**（[PortfolioManager.cs](../../src/TradingStudio.Engine/PortfolioManager.cs#L202)）
  - `Future` 增加 `FeeType` / `CloseTodayFeeRate` 字段
  - `PortfolioManager.ProcessFill` 判断当天开当天平 → 平今费率
  - `symbols.json` + `gen_symbols_json.py` 补充 75 品种平今费率
  - 关键品种：上期所 15 品种平今免、中金所 IF 平今 15x
  - 验证：与 simnow 实盘手续费偏差 < 5%

#### 🟡 P1 — 回测精度改善

- [ ] **Bar 撮合 Volume 约束** — 订单量 > Bar.Volume 时只能部分成交
- [ ] **市价单最小滑点** — 默认 1-2 跳，从 `Future.TickSize` 配置读取

#### 🟢 P2 — 策略开发效率

- [ ] **指标基类重构** — 提取 `IndicatorBase`（前向保护 + RollingWindow）
  - 重写 SMA/EMA/MACD/RSI/Bollinger 5 个指标
  - 重构后新增指标从 30min → 10min
- [ ] **StrategyContext.History()** — 策略不再直接依赖 `IBarStore`
  - `History(instrumentId, count)` 返回 `IReadOnlyList<Bar>`
  - 自动预热支持

> 回测引擎代码写完，但还没跑过一次完整的端到端回测。
> Phase 2b 精度没做 = 现在跑出来的结果不能当真。

---

## 🟡 P1 — 高优先级

### WPF 监控客户端 — 数据连接（计划 9h，布局做完内容未填）

- [ ] Phase 1（2h）：EngineHubClient SignalR 服务（连接 + 自动重连 + 7 事件绑定）
- [ ] Phase 1（2h）：ViewModelBase + INavigationAware + App.xaml.cs DI 注册
- [ ] Phase 2（3.5h）：Dashboard 4 面板布局 + DashboardViewModel（SignalR → ObservableCollection）
- [ ] Phase 2（3.5h）：Layer 0 降级（health.json 直读）
- [ ] Phase 3（3.5h）：ChartWindow 改造（InstrumentId 可绑定 + SQLite 历史 Bar 查询）
- [ ] Phase 3（3.5h）：StrategyDetailWindow + 系统托盘（NotifyIcon + 桌面通知 + 右键菜单）

> 7 个 commit 做完了 VS Code 布局框架（暗色主题、三栏、菜单栏、状态栏、多标签），
> 但 Dashboard 还是空壳，数据连接一行没写。

### 运维

- [ ] 云服务器 v0.2.0-collect 运行状态确认
- [ ] health.json 检查
- [ ] ToolBox SelfContained 发布
- [ ] 监控告警（health.json → 云端通知）

---

## 🟢 P2 — 后续计划

### 数据库切换

- [ ] DuckDB 迁移（BarStore 已加 IBarStore 接口，待实现 DuckDB 适配器）
- [ ] PostgreSQL + C# 实体模型（P0 技术债，6/14 主动延后）
  - Symbol / Contract / CommissionRule / MarginRule / TradingSession 实体
  - `TradingStudio.Data` 添加 PostgreSQL 访问层

### WPF 后续（原 Phase B/C）

- [ ] WPF Phase B：核心面板 + SignalR 实时推送
- [ ] WPF Phase C：回测集成（配置 → 执行 → 图表）

---

## 📊 Phase 3 — 品种深度研究（2026-06-18 新增）

> 详细方案：[品种深度研究计划-Phase3.md](品种深度研究计划-Phase3.md)
> 六项技术决策已确认，直接进入实现。

### 🔴 P0 — 3a 品种排名与分类（优先：确定研究范围）

- [ ] **3a-1** `analyze rank` — 多维度品种排名（成交量/持仓量/成交额），支持单年/多年汇总
  - DB 代码映射：m2→m, p2→p, i2→i, v2→v, y2→y, c2→c, l2→l, a2→a, b2→b, j2→j
  - 输出：`data/research/variety_ranking.csv`
- [ ] **3a-2** `analyze classify` — 合约周期自动分类（三主力轮换 / 多月活跃 / 单合约主导）
  - 算法：Top 3 占比 + 有效合约数 + 月度分布熵
  - 输出：`data/research/contract_cycle.csv`
- [ ] **3a-3** `analyze report` — 生成标准化品种研究报告（Markdown）
  - 输出：`docs/analysis/variety_report_{date}.md`

### 🟡 P1 — 3b 数据分层（优先：节省日常 Tick 存储）

- [ ] **3b-1** 采集分流 — CtpLiveFeed 维护 Top 30 白名单
  - Top 30 → Tick CSV + 1min Bar + Day Bar
  - 其余品种 → 1min Bar + Day Bar（跳过 Tick CSV）
- [ ] **3b-2** `tick-backfill` — 从金数源 RAR 回补 Top 30 六年历史 Tick
  - 全六年回补（2020-2025），约 19 GB
  - 新目录结构：`data/tick/{交易所}/{品种}/{年份}/`
- [ ] **3b-3** `tick-prune` — 清理非 Top 30 的 Tick CSV（支持 `--dry-run` 预览）
- [ ] **3b-4** `symbols.json` 更新 — 新增 `isTop30` + `contractCycle` 字段

### 🟢 P2 — 3c 加权连续合约

- [ ] **3c-1** `continuous build` — 加权连续合约构建（算法确认：50/50 等权）
  - 每日选取 5 个合约，权重 `w_i = normalize(0.5×vol + 0.5×oi)`，最小 5%
  - 输出：`data/continuous/{品种}_continuous.db`
- [ ] **3c-2** 换月检测 — 次主力连续 3 天成交量 > 当前主力 → 触发换月
  - 比率法回溯调整（ratio back-adjust）
  - 记录 `rollover_log`（日期、旧/新主力、价差、权重过渡）
- [ ] **3c-3** 对比验证 — vs 简单主力拼接 vs 成交量加权拼接
- [ ] **3c-4** `continuous info` + `continuous export` — 查询与导出

### 🟢 P3 — 3d 研究输出

- [ ] **3d-1** Top 30 品种深度报告（基于 3a + 3c 产出，30 份）
- [ ] **3d-2** 跨品种相关性矩阵（日收益率相关系数 + 热力图数据）
- [ ] **3d-3** 换月日历（30 品种历史换月规律汇总）

### 新增文件与目录（Phase 3）

```
新增目录：
  data/tick/                   ← 重组织 Tick 数据（按品种/年份）
  data/continuous/             ← 加权连续合约输出
  data/research/               ← 品种研究成果
  docs/analysis/               ← 品种分析报告
  src/TradingStudio.ToolBox/AnalyzeTool/     ← 品种分析命令
  src/TradingStudio.ToolBox/ContinuousTool/  ← 连续合约命令
  src/TradingStudio.ToolBox/TickTool/        ← Tick 管理命令

代码变更：
  src/TradingStudio/Live/CtpLiveFeed.cs              ← 采集分流（白名单）
  src/TradingStudio/Services/LiveDataCollector.cs     ← Tick 写入策略
  src/TradingStudio.ToolBox/ToolRegistry.cs           ← 注册 8 个新命令
  src/TradingStudio/symbols.json                      ← +isTop30 +contractCycle
```

---

## 📋 对照：三天计划完成与未完成

| 来源 | 计划项 | 完成 | 未完成 |
|------|--------|:----:|:------:|
| 6/14 | 回测引擎 Bar + Tick | ✅ | |
| 6/14 | v0.2.0 云端部署 | ✅ | |
| 6/14 | 全量验证 2020-2025 | ✅ | |
| 6/14 | 实盘引擎骨架 | ✅ | |
| 6/14 | 多策略端到端验证 | | ⬜ |
| 6/14 | CTP TraderApi 字段映射 | | ⬜ |
| 6/14 | Simnow 验证 | | ⬜ |
| 6/14 | PostgreSQL 实体 | | ⬜ |
| 6/14 | ToolBox SelfContained | | ⬜ |
| 6/14 | 监控告警 | | ⬜ |
| 6/15 | WPF Phase A 骨架 | ✅ | (方案改为原生 WPF) |
| 6/15 | 2021-2025 导入 | ✅ | |
| 6/15 | Simnow 验证 | | ⬜ |
| 6/16 | 数据全量验证 | ✅ | |
| 6/16 | WPF Phase 1 数据连接 | | ⬜ |
| 6/16 | WPF Phase 2 Dashboard | | ⬜ |
| 6/16 | WPF Phase 3 Chart + 托盘 | | ⬜ |
| 6/16 | Simnow 验证 | | ⬜ |
| 6/16 | 运维确认 | | ⬜ |
