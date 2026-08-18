#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Calculate commodity index proxy from futures data"""
import pickle, os, json

VAULT = r"C:\Users\chenx\OneDrive\MyFiles\DialyNotes\Trading"

with open(os.path.join(VAULT, "scripts", "raw_data.pkl"), "rb") as f:
    data = pickle.load(f)

futures = data.get("futures", {})

categories = {
    "贵金属": ["AG2609"],
    "有色金属": ["CU2609", "AL2609", "ZN2609", "NI2609", "SN2609", "PB2609"],
    "能源": ["SC2609", "FU2609"],
    "化工": ["TA2609", "MA2609", "V2609", "L2609", "EG2609"],
    "黑色系": ["RB2609", "HC2609", "I2609", "JM2609", "J2609"],
    "农产品": ["M2609", "Y2609", "P2609", "SR2609", "CF2609", "RM2609"],
    "新能源": ["LC2609", "SI2609"],
}

# Weight by sector count
sector_weights = {
    "贵金属": 2, "有色金属": 6, "能源": 2,
    "化工": 5, "黑色系": 5, "农产品": 6, "新能源": 2
}

print("=" * 55)
print("  商品指数估算（基于28个跟踪合约等权计算）")
print("=" * 55)
print()

all_changes = []
sector_results = {}

for cat, syms in categories.items():
    cat_changes = []
    for s in syms:
        if s in futures and futures[s].get("chg_pct") is not None:
            cat_changes.append(futures[s]["chg_pct"])
            all_changes.append(futures[s]["chg_pct"])
    if cat_changes:
        avg = sum(cat_changes) / len(cat_changes)
        sector_results[cat] = avg
        print(f"  {cat:8s} : {avg:+.2f}% ({len(cat_changes)}个合约)")

print()
print("-" * 55)

if all_changes:
    # Simple average of all contracts
    simple_avg = sum(all_changes) / len(all_changes)

    # Weighted average by sector
    weighted_sum = 0
    total_w = 0
    for cat, avg in sector_results.items():
        w = sector_weights.get(cat, 1)
        weighted_sum += avg * w
        total_w += w
    weighted_avg = weighted_sum / total_w

    up = len([c for c in all_changes if c > 0])
    down = len([c for c in all_changes if c < 0])

    print(f"  综合商品指数（简单平均）: {simple_avg:+.2f}%")
    print(f"  综合商品指数（板块加权）: {weighted_avg:+.2f}%")
    print(f"  上涨/下跌: {up}涨 / {down}跌 / {len(all_changes)}总")
    print()

    # Trend direction
    if weighted_avg > 0.5:
        direction = "偏强 "
    elif weighted_avg > -0.5:
        direction = "震荡（窄幅）"
    elif weighted_avg > -1.5:
        direction = "偏弱 "
    else:
        direction = "显著下跌"

    print(f"  方向判断: {direction}")
    print(f"  最强板块: {max(sector_results, key=sector_results.get)} ({max(sector_results.values()):+.2f}%)")
    print(f"  最弱板块: {min(sector_results, key=sector_results.get)} ({min(sector_results.values()):+.2f}%)")
    print()

    print("  说明: 南华商品指数/文华商品指数为商业数据,")
    print("        免费API无法直接获取。以上为基于跟踪品种的估算值。")

print()
print("=" * 55)
