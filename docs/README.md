# TradingStudio 文档索引

> 最后更新：2026-06-11

---

## 文档地图

```
01-first-principles-analysis.md  ← 起点：从第一性原理推导系统需求
        │
        ├→ 02-data-model-spec.md         ← Tick/Bar 数据格式权威定义
        ├→ 04-logging-feedback.md        ← 日志与反馈架构
        ├→ 05-futures-contract-model.md  ← 合约模型三层设计
        │
        ├→ 03-ctp-wrapper-design.md      ← [已废弃] P/Invoke 方案
        │   └→ 07-ctp-cli-wrapper-plan.md ← [已废弃] C++/CLI 方案
        │       └→ 09-ctp-interface-analysis.md ← ✅ CTP 字段缺口分析
        │
        ├→ 08-contract-data-analysis.md  ← [已吸收] 76 品种数据分析
        │   └→ 10-data-model-reconciled.md ← ✅ 最终数据模型 + DDL
        │
        └→ 11-implementation-roadmap-v2.md ← ✅ 当前状态 + 实施路线

外部参考：
        CTP接口封装方案.md                 ← ChatGPT 对话（C++/CLI 方案参考）
        Lean引擎架构分析.md                ← Lean Engine 源码学习笔记
        TradingStudio架构设计-精简版.md     ← ✅ 当前架构设计标准
```

---

## 文档状态一览

### ✅ 活跃（当前遵循）

| # | 文件 | 内容 | 何时读 |
|---|------|------|--------|
| 01 | `01-first-principles-analysis.md` | 系统第一性原理推导、7 大约束、优先级排序 | 需要确认"我们要做什么、不做什么"时 |
| 02 | `02-data-model-spec.md` | TickRecord (80B)、.tick 二进制格式、Bar 模型 | 实现 Tick 存储或 Bar 聚合时 |
| 04 | `04-logging-feedback.md` | 4 层日志、Serilog 配置、健康检查端点 | 实现日志基础设施时 |
| 05 | `05-futures-contract-model.md` | Exchange→Symbol→Contract 三层实体设计 | 实现合约数据存储时 |
| 09 | `09-ctp-interface-analysis.md` | CTP 42 字段清单、12/42 缺口分析、P0 修复方案 | 修改 CTP 桥接代码前必读 |
| 10 | `10-data-model-reconciled.md` | 合并 05+08+09 后的最终 PostgreSQL DDL | 建表 / 写 Entity 时 |
| 11 | `11-implementation-roadmap-v2.md` | 代码现状审计 + 6 步实施计划 | 每次开始新的实现步骤前 |
| — | `TradingStudio架构设计-精简版.md` | 从 Lean 学习后精简的 5 项目架构标准 | 做架构决策、加新模块时 |

### ❌ 已废弃（不要按它实现）

| # | 文件 | 废弃原因 |
|---|------|---------|
| 03 | `03-ctp-wrapper-design.md` | 描述纯 P/Invoke 方案，实际代码用的是 C++ 原生桥接 + P/Invoke 混合方案 |
| 07 | `07-ctp-cli-wrapper-plan.md` | 描述 C++/CLI 方案，实际代码走了 C++ 原生桥接路线（更简单） |
| 08 | `08-contract-data-analysis.md` | 76 品种数据分析已全部吸收进 10（最终 DDL） |
| 06 | `06-data-layer-architecture.md` | 项目结构描述与实际代码不符（写于代码重构前） |

### 📎 参考

| 文件 | 内容 | 用途 |
|------|------|------|
| `实际行情数据分析-CU1603.md` | CU1603 真实 Tick 数据完整分析 | 用数据验证设计：48K ticks/天、时段分布、CTP 字段映射 |
| `CTP接口封装方案.md` | ChatGPT 对话，C++/CLI 封装方案 | C++/CLI 技术参考，不作为项目决策 |
| `Lean引擎架构分析.md` | Lean Engine 源码架构分析 | 设计模式参考，学习笔记 |
| `架构验证报告.md` | 16 份文档交叉验证 + 代码审计 | 检测设计冲突和缺口 |

---

## 快速导航

### 我要开始写代码，先读什么？

1. [TradingStudio架构设计-精简版.md](TradingStudio架构设计-精简版.md) — 知道 5 个项目的定位
2. [11-implementation-roadmap-v2.md](11-implementation-roadmap-v2.md) — 知道当前代码在哪、下一步做什么
3. [02-data-model-spec.md](02-data-model-spec.md) — 知道 TickRecord 和 Bar 的精确定义
4. [10-data-model-reconciled.md](10-data-model-reconciled.md) — 知道数据库表结构

### 我要修改 CTP 桥接，先读什么？

1. [09-ctp-interface-analysis.md](09-ctp-interface-analysis.md) — P0 缺口分析
2. 实际代码：`src/CTP/CTPWrapper/CtpMdBridge.cpp` + `src/TradingStudioBridge/CtpInterop.cs`

### 我要了解为什么这样设计，先读什么？

1. [01-first-principles-analysis.md](01-first-principles-analysis.md) — 第一性原理
2. [TradingStudio架构设计-精简版.md](TradingStudio架构设计-精简版.md) — 为什么砍掉那么多东西

---

## 文档链

编号文档 01→11 形成设计推导链：

```
01 (需求推导)
 ├→ 02 (数据格式) ──────────────────────┐
 ├→ 03→07→09 (CTP 封装路线) ────────────┤
 ├→ 04 (日志)                           ├→ 10 (最终 DDL) → 11 (路线图)
 ├→ 05 (合约模型) ───┐                  │
 └→ 08 (合约分析) ───┤                  │
                    └→ 合并 ────────────┘
```

---

## 合约数据

| 目录 | 内容 | 数量 |
|------|------|------|
| `contracts/SHFE/` | 上期所品种规格 | 19 |
| `contracts/INE/` | 上能源品种规格 | 5 |
| `contracts/DCE/` | 大商所品种规格 | 22 |
| `contracts/CZCE/` | 郑商所品种规格 | 19 |
| `contracts/CFFEX/` | 中金所品种规格 | 8 |
| `contracts/GFEX/` | 广期所品种规格 | 3 |
| `contracts/main_contracts/` | 主力合约切换 CSV | 38 |
| `contracts/all_contracts/` | 全合约上市 CSV | 22 |

> 合约规格的权威数据源是 Obsidian 知识库 `04-Research/04-交易系统/六大交易所合约规格表.md`。
> `gen_final_specs.py` 通过 AKShare 实时拉取并更新。
