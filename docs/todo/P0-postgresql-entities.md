# P0 — PostgreSQL 数据库 + C# 实体模型

> 依据: doc 10 DDL（7 张表）
> 状态: 未开始
> 阻塞: 无（但延迟越久，SQLite 与正式 schema 的落差越大）

## 任务

1. 本地 PostgreSQL 执行 doc 10 完整 DDL
2. 创建 `TradingStudio.Core/Models/` C# record:
   - `Exchange.cs` ✅ 已有 (enum)
   - `Symbol.cs` — 品种定义
   - `Contract.cs` — 合约实例
   - `CommissionRule.cs` — 手续费规则
   - `MarginRule.cs` — 保证金规则
   - `TradingSession.cs` — 交易时段
3. `TradingStudio.Data` 添加 PostgreSQL 访问层

## 关联

- [[../design/10-data-model-reconciled]]
- [[../design/11-implementation-roadmap-v2]] Step 1
