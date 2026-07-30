---
name: next-week-todos
description: 下周待办 7/26-8/1 — 收尾Phase2 + 推进Phase4实盘验证
metadata: 
  node_type: memory
  type: project
  originSessionId: c1ace19e-0bd1-478f-a0ae-9b732d7ae630
  modified: 2026-07-24T22:39:49.759Z
---

## 下周待办（7/26 — 8/1）

### 周一-二：数据质量收尾
- [ ] 修复 Data 测试 6/28 — bars_history.duckdb 路径
- [ ] 文华 K 线交叉验证 — ag2612 5min Bar OHLCV 对照
- [ ] 连续合约 xxx000 生成修复

### 周三-四：实盘链路
- [ ] 解决 simnow 开户权限
- [ ] 订单事件持久化（OrderEvent → SQLite/DuckDB）
- [ ] Live 模式日盘端到端验证

### 周五-六：策略迭代
- [ ] MaCross 回测 2024-2026
- [ ] AtrTrail 止损 Live 验证
- [ ] Collect + Live 分时并行

### 周日：复盘
- [ ] K 线验证报告
- [ ] 更新 CLAUDE.md 状态
- [ ] 提交 + 推送

**Why:** 当前项目阻塞在 simnow 账户 + 数据验证，需要一周攻克。
**How to apply:** 每日对照推进，阻塞项优先。
