"""
Factor Mining Phase 1: 综合报告
因子筛选 (IC_IR + 低相关性) + Markdown 报告

用法: cd TradingStudio && python scripts/factor_research/factor_report.py

输入: output/ic_summary.csv + output/quantile_results.csv
输出: output/factor_report_v1.md (Obsidian-ready)
"""

import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

import numpy as np
import pandas as pd
from datetime import datetime
import warnings
warnings.filterwarnings('ignore')

# ── 配置 ──────────────────────────────────────────

OUTPUT_DIR = "scripts/factor_research/output"
IC_PATH   = os.path.join(OUTPUT_DIR, "ic_summary.csv")
QR_PATH   = os.path.join(OUTPUT_DIR, "quantile_results.csv")
PANEL_PATH = os.path.join(OUTPUT_DIR, "factors_panel_v1_clean.parquet")
REPORT_PATH = os.path.join(OUTPUT_DIR, "factor_report_v1.md")

IC_IR_THRESHOLD = 0.15   # 最低 IC_IR (放宽, 在此阶段不筛掉太多)
CORR_THRESHOLD  = 0.70   # 共线性阈值

print("=" * 70)
print("Factor Report: 因子筛选 + Markdown 报告")
print("=" * 70)

# ── 加载 ──────────────────────────────────────────

ic_df  = pd.read_csv(IC_PATH)
qr_df  = pd.read_csv(QR_PATH)
panel  = pd.read_parquet(PANEL_PATH)

factor_cols = [c for c in panel.columns if c not in
    ['instrument_id', 'bar_time', 'period', 'FwdRet_1d']]

# ── 1. 因子综合评分 ────────────────────────────────

print("\n[1/4] 计算因子综合评分...")

# IS IC_IR (horizon=1d) 作为主排序
scores = ic_df[(ic_df['period'] == 'IS') & (ic_df['horizon'] == 1)].copy()
scores = scores[['factor', 'IC_mean', 'IC_IR', 'pos_ratio']].copy()
scores = scores.rename(columns={'IC_IR': 'IS_IC_IR', 'IC_mean': 'IS_IC_mean', 'pos_ratio': 'IS_pos_ratio'})

# 合并 OOS IC_IR
oos = ic_df[(ic_df['period'] == 'OOS') & (ic_df['horizon'] == 1)][['factor', 'IC_IR', 'pos_ratio']]
oos = oos.rename(columns={'IC_IR': 'OOS_IC_IR', 'pos_ratio': 'OOS_pos_ratio'})
scores = scores.merge(oos, on='factor', how='left')

# OOS/IS 稳定性
scores['stability'] = scores['OOS_IC_IR'] / scores['IS_IC_IR'].abs()
scores['stability'] = scores['stability'].clip(-2, 2)

# 合并 LS Sharpe (OOS)
ls = qr_df[(qr_df['period'] == 'OOS') & (qr_df['quantile'] == 'LS')][['factor', 'sharpe', 'ann_ret', 'max_dd']]
ls = ls.rename(columns={'sharpe': 'LS_sharpe', 'ann_ret': 'LS_ann_ret', 'max_dd': 'LS_max_dd'})
scores = scores.merge(ls, on='factor', how='left')

# 填充
scores[['OOS_IC_IR', 'OOS_pos_ratio', 'stability', 'LS_sharpe', 'LS_ann_ret', 'LS_max_dd']] = \
    scores[['OOS_IC_IR', 'OOS_pos_ratio', 'stability', 'LS_sharpe', 'LS_ann_ret', 'LS_max_dd']].fillna(0)

# 综合得分: 0.4*IS_IC_IR + 0.3*stability + 0.2*LS_sharpe + 0.1*OOS_IC_IR
scores['composite'] = (
    0.4 * scores['IS_IC_IR'].abs() +
    0.3 * scores['stability'].clip(0, 1) +
    0.2 * scores['LS_sharpe'].clip(0, 1) +
    0.1 * scores['OOS_IC_IR'].abs()
)
scores = scores.sort_values('composite', ascending=False).reset_index(drop=True)

# ── 2. 因子筛选 (去共线性) ────────────────────────

print("[2/4] 去共线性筛选...")

# 计算因子相关性矩阵
valid = panel[factor_cols].dropna()
corr = valid.corr()

# Greedy selection: 按 composite score 从高到低, 保留 corr < threshold
selected = []
for _, row in scores.iterrows():
    fac = row['factor']
    if fac not in corr.columns:
        continue
    too_correlated = False
    for sel in selected:
        if abs(corr.loc[fac, sel]) > CORR_THRESHOLD:
            too_correlated = True
            break
    if not too_correlated:
        selected.append(fac)

print(f"  入选因子: {len(selected)}/{len(factor_cols)}")
for i, fac in enumerate(selected):
    s = scores[scores['factor'] == fac].iloc[0]
    print(f"  {i+1}. {fac:20s}  IS_IC_IR={s['IS_IC_IR']:+.3f}  OOS_IC_IR={s['OOS_IC_IR']:+.3f}  LS_Sharpe={s['LS_sharpe']:+.3f}")

# ── 3. 相关性热力图 (文本版) ──────────────────────

print(f"\n[3/4] 入选因子相关性矩阵...")
if len(selected) >= 2:
    sel_corr = corr.loc[selected, selected]
    print(f"{'':>20s}", end="")
    for c in selected:
        print(f" {c[:8]:>8s}", end="")
    print()
    for r in selected:
        print(f"{r:>20s}", end="")
        for c in selected:
            v = sel_corr.loc[r, c]
            print(f" {v:8.3f}", end="")
        print()

# ── 4. 生成 Markdown 报告 ──────────────────────────

print(f"\n[4/4] 生成 Markdown 报告...")

report_lines = []
report_lines.append(f"---")
report_lines.append(f"date: {datetime.now().strftime('%Y-%m-%d')}")
report_lines.append(f"tags: [因子研究, IC分析, 分组回测, Phase1]")
report_lines.append(f"status: 完成")
report_lines.append(f"---")
report_lines.append(f"")
report_lines.append(f"# 因子挖掘 Phase 1 报告 — {datetime.now().strftime('%Y-%m-%d')}")
report_lines.append(f"")
report_lines.append(f"> 数据: bars_history.duckdb, 2020-2026, 50+ 连续合约")
report_lines.append(f"> 因子: {len(factor_cols)} 个 (日频趋势/波动率 + 1min微观结构)")
report_lines.append(f"> IS: 2020-2023 | OOS: 2024-2026")
report_lines.append(f"")
report_lines.append(f"## 1. 因子总排名 (综合得分)")
report_lines.append(f"")
report_lines.append(f"| Rank | Factor | IS IC_IR | OOS IC_IR | Stability | LS Sharpe | Composite |")
report_lines.append(f"|------|--------|----------|-----------|-----------|-----------|-----------|")
for i, (_, row) in enumerate(scores.head(15).iterrows()):
    sel_mark = " ✓" if row['factor'] in selected else ""
    report_lines.append(f"| {i+1} | {row['factor']}{sel_mark} | {row['IS_IC_IR']:+.4f} | {row['OOS_IC_IR']:+.4f} | {row['stability']:.2f} | {row['LS_sharpe']:+.3f} | {row['composite']:.3f} |")

report_lines.append(f"")
report_lines.append(f"## 2. 入选因子 (去共线性, r < {CORR_THRESHOLD})")
report_lines.append(f"")
report_lines.append(f"| # | Factor | IS IC_IR | OOS IC_IR | Stability | LS Sharpe | LS AnnRet | LS MaxDD |")
report_lines.append(f"|---|--------|----------|-----------|-----------|-----------|-----------|----------|")
for i, fac in enumerate(selected):
    s = scores[scores['factor'] == fac].iloc[0]
    report_lines.append(f"| {i+1} | {fac} | {s['IS_IC_IR']:+.4f} | {s['OOS_IC_IR']:+.4f} | {s['stability']:.2f} | {s['LS_sharpe']:+.3f} | {s['LS_ann_ret']:+.2%} | {s['LS_max_dd']:+.2%} |")

report_lines.append(f"")
report_lines.append(f"## 3. 因子相关性矩阵 (入选)")
report_lines.append(f"")
if len(selected) >= 2:
    report_lines.append(f"```")
    report_lines.append(f"{'':>20s} " + " ".join(f"{c[:8]:>8s}" for c in selected))
    for r in selected:
        report_lines.append(f"{r:>20s} " + " ".join(f"{sel_corr.loc[r,c]:8.3f}" for c in selected))
    report_lines.append(f"```")

report_lines.append(f"")
report_lines.append(f"## 4. IC 衰减 (IS)")
report_lines.append(f"")
for fac in selected[:6]:
    rows = ic_df[(ic_df['factor'] == fac) & (ic_df['period'] == 'IS')].sort_values('horizon')
    ics = [f"h={int(r['horizon'])}d: {r['IC_mean']:+.4f}" for _, r in rows.iterrows()]
    report_lines.append(f"- **{fac}**: {', '.join(ics)}")

report_lines.append(f"")
report_lines.append(f"## 5. 后续行动")
report_lines.append(f"")
report_lines.append(f"- [ ] 入选因子 C# FactorBase 子类实现 → 接入 live 交易")
report_lines.append(f"- [ ] 因子合成 (Ridge → LightGBM) → Phase 2")
report_lines.append(f"- [ ] 滚动 IC 面板监控 (每日更新)")
report_lines.append(f"- [ ] Walk-forward validation → 杜绝过拟合")
report_lines.append(f"")
report_lines.append(f"---")
report_lines.append(f"**综合得分公式**: 0.4×|IS_IC_IR| + 0.3×Stability + 0.2×LS_Sharpe + 0.1×|OOS_IC_IR|")

with open(REPORT_PATH, 'w', encoding='utf-8') as f:
    f.write('\n'.join(report_lines))

print(f"[SAVED] {REPORT_PATH}")
print(f"\n{'='*70}")
print(f"Phase 1 完成!")
print(f"{'='*70}")
print(f"\n输出文件:")
print(f"  {os.path.join(OUTPUT_DIR, 'factors_panel_v1.parquet')}")
print(f"  {os.path.join(OUTPUT_DIR, 'factors_panel_v1_clean.parquet')}")
print(f"  {IC_PATH}")
print(f"  {QR_PATH}")
print(f"  {REPORT_PATH}")
print(f"\n入选因子: {', '.join(selected)}")
