"""
Factor Mining Phase 1: 因子预处理
MAD去极值 + 截面Z-score标准化

用法: cd TradingStudio && python scripts/factor_research/factor_preprocess.py

输入: output/factors_panel_v1.parquet
输出: output/factors_panel_v1_clean.parquet
"""

import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

import numpy as np
import pandas as pd
import warnings
warnings.filterwarnings('ignore')

# ── 配置 ──────────────────────────────────────────

OUTPUT_DIR = "scripts/factor_research/output"
INPUT_PATH  = os.path.join(OUTPUT_DIR, "factors_panel_v1.parquet")
OUTPUT_PATH = os.path.join(OUTPUT_DIR, "factors_panel_v1_clean.parquet")

MAD_MULTIPLIER = 5.0  # MAD 倍数阈值
MIN_SAMPLES_PER_DAY = 5  # 每天至少需要5个品种才做截面标准化

print("=" * 70)
print("Factor Preprocessing: MAD Winsorize + Cross-Sectional Z-Score")
print("=" * 70)

# ── 加载 ──────────────────────────────────────────

panel = pd.read_parquet(INPUT_PATH)
print(f"\n加载: {len(panel):,} 行, {panel['instrument_id'].nunique()} 品种")

# 因子列 (排除非因子列)
exclude_cols = ['instrument_id', 'trading_day', 'bar_time', 'period', 'FwdRet_1d']
factor_cols = [c for c in panel.columns if c not in exclude_cols]
print(f"因子: {len(factor_cols)} 个")

# ── 1. MAD 去极值 (全样本, 非时序) ──────────────────

print(f"\n[1/3] MAD 去极值 (×{MAD_MULTIPLIER})...")

def mad_winsorize(series: pd.Series) -> pd.Series:
    """MAD法去极值: 超出 median ± N*MAD 的值截断"""
    med = series.median()
    mad = (series - med).abs().median()
    if mad == 0 or np.isnan(mad):
        return series
    upper = med + MAD_MULTIPLIER * mad
    lower = med - MAD_MULTIPLIER * mad
    return series.clip(lower, upper)

for col in factor_cols:
    before_nan = panel[col].isna().mean()
    panel[col] = panel.groupby('instrument_id')[col].transform(mad_winsorize)
    after_nan = panel[col].isna().mean()
    clipped_pct = (before_nan != after_nan)

stats_before = panel[factor_cols].describe().loc[['min', 'max', 'mean', 'std']]

# ── 2. 截面 Z-score 标准化 ────────────────────────

print("[2/3] 截面 Z-score 标准化 (每天横截面内)...")

print("[2/3] 截面 Z-score 标准化 (每天横截面内, groupby.transform)...")

for c in factor_cols:
    grp = panel.groupby('bar_time')[c]
    mu = grp.transform('mean')
    sigma = grp.transform('std')
    # 小样本天设为 NaN
    counts = grp.transform('count')
    mask = counts < MIN_SAMPLES_PER_DAY
    z = (panel[c] - mu) / sigma.replace(0, np.nan)
    z[mask] = np.nan
    panel[c] = z

# ── 3. 质量控制 ────────────────────────────────────

print("[3/3] 质量检查...")

stats_after = panel[factor_cols].describe().loc[['min', 'max', 'mean', 'std']]

# 因子相关性矩阵 (Pearson, 去除NaN)
valid_mask = panel[factor_cols].notna().all(axis=1)
corr_matrix = panel.loc[valid_mask, factor_cols].corr()
high_corr_pairs = []
for i in range(len(factor_cols)):
    for j in range(i+1, len(factor_cols)):
        r = corr_matrix.iloc[i, j]
        if abs(r) > 0.85:
            high_corr_pairs.append((factor_cols[i], factor_cols[j], r))

print(f"\n── 预处理前后对比 ──")
print(f"  去极值前 std:  {stats_before.loc['std'].mean():.4f}")
print(f"  去极值后 std:  {stats_after.loc['std'].mean():.4f}")
print(f"  有效样本:      {valid_mask.sum():,} / {len(panel):,}")

if high_corr_pairs:
    print(f"\n── 高相关性警告 (>0.85) ──")
    for a, b, r in high_corr_pairs:
        print(f"  {a:20s} × {b:20s}  r={r:+.3f}")

# ── 保存 ──────────────────────────────────────────

panel.to_parquet(OUTPUT_PATH, index=False)
print(f"\n[SAVED] {OUTPUT_PATH}")
print(f"  文件大小: {os.path.getsize(OUTPUT_PATH)/1024/1024:.1f} MB")
print(f"\n下一步: python scripts/factor_research/factor_ic.py")
