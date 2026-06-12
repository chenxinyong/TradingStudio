---
name: 2026-06-12-cli-args-filter
description: 命令行参数支持 — exchange/symbol 过滤，用法 TradingStudio [SHFE] [ag|ag2608]
metadata:
  type: project
---

## 用法

```bash
TradingStudio                      # 全市场 74品种 928合约
TradingStudio SHFE                 # 上期所所有品种
TradingStudio SHFE ag              # 白银所有合约 (ag2606~ag2712)
TradingStudio SHFE ag2608          # 仅 ag2608
TradingStudio DCE i                # 大商所铁矿石所有合约
```

## 实现

- Program.cs: 解析 args[0]=exchange, args[1]=symbol/contract
- CollectOptions.ExchangeFilter / SymbolFilter (命令行注入)
- CollectService: 加载后按 exchange → symbol → contract 三级过滤
- 含数字 = 精确合约，不含数字 = 品种前缀
