# -*- coding: utf-8 -*-
"""
跨品种 IC 对比 — IntradayMom / VWAP_Dev 在不同品种板块上的 IC 差异。

目的: 回答「日内动量和 VWAP 偏离这两个有效因子，在贵金属/黑色/化工等板块上
      IC 是否一致，还是集中在某几个板块」。若板块 IC 差异大，说明因子是
      板块特异性信号，实盘应只在该板块的品种上应用。

方法 (与 factor_ic.py 一致):
  - Rank IC = 每日横截面 Spearman(factor_t, FwdRet_1d)
  - IC_mean / IC_std / IC_IR = mean/std / pos_ratio / t_stat
  - IS (2020-2023) vs OOS (2024-2026) 分别统计

输入: output/factors_panel_v1_clean.parquet + src/TradingStudio/symbols.json
输出: output/cross_variety_ic.csv + 控制台对比表
"""
import json
import pandas as pd
import numpy as np
from scipy.stats import spearmanr

PANEL = "scripts/factor_research/output/factors_panel_v1_clean.parquet"
SYMBOLS = "src/TradingStudio/symbols.json"
FACTORS = ["IntradayMom", "VWAP_Dev"]
TARGET_CATEGORIES = ["贵金属", "黑色金属", "化工"]  # 本周任务聚焦三个板块

# ── 1. 品种 → 板块映射 ──
with open(SYMBOLS, encoding="utf-8") as f:
    syms = json.load(f)["symbols"]
code2cat = {s["code"].lower(): s["category"] for s in syms}

def to_category(instrument_id: str) -> str:
    base = instrument_id[:-3].lower() if instrument_id.endswith("000") else instrument_id.lower()
    return code2cat.get(base, "其他")

# ── 2. 加载 panel ──
df = pd.read_parquet(PANEL)
df["category"] = df["instrument_id"].map(to_category)
print(f"Panel: {len(df)} rows, {df['instrument_id'].nunique()} instruments")
print("板块分布:")
print(df.groupby("category")["instrument_id"].nunique().to_string())

# ── 3. 每板块 × 每因子 × IS/OOS 的 Rank IC ──
# 品种数阈值设为 2：贵金属只有 ag/au 两个品种，仍纳入但截面 IC 会退化（±1 二元），报告加注。
rows = []
for cat, sub in df.groupby("category"):
    n_inst = sub["instrument_id"].nunique()
    if n_inst < 2:
        continue
    for period, subp in sub.groupby("period"):
        for factor in FACTORS:
            daily_ic = []
            for day, d in subp.groupby("trading_day"):
                valid = d[[factor, "FwdRet_1d"]].dropna()
                if len(valid) < 2:
                    continue
                rho, _ = spearmanr(valid[factor], valid["FwdRet_1d"])
                if not np.isnan(rho):
                    daily_ic.append(rho)
            if len(daily_ic) < 10:
                continue
            ic = np.array(daily_ic)
            mean, std = ic.mean(), ic.std(ddof=1)
            rows.append({
                "category": cat, "n_instruments": n_inst,
                "period": period, "factor": factor,
                "N_days": len(ic),
                "IC_mean": mean, "IC_std": std,
                "IC_IR": mean / std if std > 0 else np.nan,
                "pos_ratio": float((ic > 0).mean()),
                "t_stat": mean / std * np.sqrt(len(ic)) if std > 0 else np.nan,
            })

res = pd.DataFrame(rows)
res.to_csv("scripts/factor_research/output/cross_variety_ic.csv", index=False, encoding="utf-8-sig")

# ── 4. 生成 markdown 报告 ──
lines = []
lines.append("# 跨品种 IC 对比 — IntradayMom / VWAP_Dev")
lines.append("")
lines.append("> Rank IC = 每日横截面 Spearman(factor_t, FwdRet_1d)。IS 2020-2023 / OOS 2024-2026。")
lines.append("")
lines.append(f"**Panel**: {len(df)} rows, {df['instrument_id'].nunique()} 品种 (xxx000 连续合约)。")
lines.append("")
lines.append("## 板块品种数")
lines.append("")
lines.append("| 板块 | 品种数 |")
lines.append("|------|:--:|")
for cat, n in df.groupby("category")["instrument_id"].nunique().sort_values(ascending=False).items():
    lines.append(f"| {cat} | {n} |")
lines.append("")
lines.append("## 因子 IC_IR 对比 (按 |OOS| 排序)")
lines.append("")
for factor in FACTORS:
    lines.append(f"### {factor}")
    lines.append("")
    pivot = res[res["factor"] == factor].pivot_table(
        index="category", columns="period", values="IC_IR", aggfunc="first")
    pivot = pivot.reindex(columns=["IS", "OOS"])
    pivot["|OOS|"] = pivot["OOS"].abs()
    pivot = pivot.sort_values("|OOS|", ascending=False).drop(columns="|OOS|")
    lines.append("| 板块 | IS IC_IR | OOS IC_IR |")
    lines.append("|------|:--:|:--:|")
    for cat, row in pivot.iterrows():
        lines.append(f"| {cat} | {row['IS']:.3f} | {row['OOS']:.3f} |")
    lines.append("")

lines.append("## 结论")
lines.append("")
lines.append("- **贵金属只有 ag/au 两个品种**，截面 Rank IC 退化为二元 ±1，IC 不可靠（表中数值仅供记录，不作结论依据）。")
lines.append("- 其余板块 (化工 21 / 农产品 18 / 黑色 9 / 有色 8 / 能源 5) 截面 IC 可靠。")
lines.append("")
report_md = "\n".join(lines)
with open("scripts/factor_research/output/cross_variety_ic.md", "w", encoding="utf-8") as f:
    f.write(report_md)

print(report_md)

