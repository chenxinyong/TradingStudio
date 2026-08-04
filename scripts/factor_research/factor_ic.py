"""
Factor Mining Phase 1: Rank IC 分析
Spearman 相关系数: factor_t vs forward_return_t+1

用法: cd TradingStudio && python scripts/factor_research/factor_ic.py

输入: output/factors_panel_v1_clean.parquet
输出: output/ic_summary.csv + 终端报告
"""

import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

import numpy as np
import pandas as pd
from scipy import stats
import warnings
warnings.filterwarnings('ignore')

# ── 配置 ──────────────────────────────────────────

OUTPUT_DIR = "scripts/factor_research/output"
INPUT_PATH = os.path.join(OUTPUT_DIR, "factors_panel_v1_clean.parquet")
IC_SUMMARY_PATH = os.path.join(OUTPUT_DIR, "ic_summary.csv")

FORWARD_PERIODS = [1, 5, 20]  # 前瞻期数 (1d, 5d, 20d)
MIN_SAMPLES_PER_DAY = 5

print("=" * 70)
print("Factor IC Analysis: Rank IC (Spearman)")
print("=" * 70)

# ── 加载 ──────────────────────────────────────────

panel = pd.read_parquet(INPUT_PATH)
panel['bar_time'] = pd.to_datetime(panel['bar_time'])
print(f"\n加载: {len(panel):,} 行, {panel['instrument_id'].nunique()} 品种")

factor_cols = [c for c in panel.columns if c not in
    ['instrument_id', 'bar_time', 'period', 'FwdRet_1d']]

# ── 构建前瞻收益 ──────────────────────────────────

print(f"构建前瞻收益 (horizon={FORWARD_PERIODS})...")

# 按品种排序
panel = panel.sort_values(['instrument_id', 'bar_time']).reset_index(drop=True)

# 日收益
panel['ret_1d'] = panel.groupby('instrument_id')['FwdRet_1d'].shift(0)  # 原始就是 t→t+1

# 多期前瞻收益 (t+1 → t+N)
for h in FORWARD_PERIODS:
    if h == 1:
        panel[f'fwd_ret_{h}d'] = panel['ret_1d']
    else:
        # compound return over h days
        panel[f'cum_ret'] = panel.groupby('instrument_id')['ret_1d'].transform(
            lambda x: x.rolling(h, min_periods=h).apply(
                lambda r: np.prod(1 + r) - 1, raw=True))
        panel[f'fwd_ret_{h}d'] = panel.groupby('instrument_id')[f'cum_ret'].shift(-(h-1))

# ── Rank IC 计算 ──────────────────────────────────

def compute_ic_series(factor_values: pd.Series, fwd_returns: pd.Series,
                      group_key: pd.Series, min_samples: int = MIN_SAMPLES_PER_DAY):
    """对每天的横截面计算 Rank IC (Spearman)"""
    df = pd.DataFrame({'factor': factor_values, 'fwd_ret': fwd_returns, 'day': group_key})
    df = df.dropna(subset=['factor', 'fwd_ret'])

    def _day_ic(grp):
        if len(grp) < min_samples:
            return np.nan
        # 确保只取 factor 和 fwd_ret 列 (避免 group key 混入)
        try:
            r, _ = stats.spearmanr(grp['factor'].values, grp['fwd_ret'].values)
        except Exception:
            return np.nan
        return r

    return df.groupby('day', group_keys=False).apply(_day_ic).dropna()

print(f"\n计算 Rank IC...")

results = []
for h in FORWARD_PERIODS:
    print(f"  horizon={h}d...")
    fwd_col = f'fwd_ret_{h}d'
    for fac in factor_cols:
        ic_series_full = compute_ic_series(
            panel[fac], panel[fwd_col], panel['bar_time'])

        if len(ic_series_full) == 0:
            continue

        # Split IS/OOS
        ic_dates = pd.to_datetime(ic_series_full.index)
        is_ic = ic_series_full[ic_dates <= '2023-12-31']
        oos_ic = ic_series_full[ic_dates > '2023-12-31']

        for label, ic_series in [('IS', is_ic), ('OOS', oos_ic)]:
            if len(ic_series) < 20:
                continue
            ic_mean = ic_series.mean()
            ic_std  = ic_series.std()
            ic_ir   = ic_mean / ic_std if ic_std > 0 else 0
            pos_ratio = (ic_series > 0).mean()
            t_stat = ic_mean / (ic_std / np.sqrt(len(ic_series))) if ic_std > 0 else 0

            results.append({
                'factor': fac,
                'horizon': h,
                'period': label,
                'N_days': len(ic_series),
                'IC_mean': round(ic_mean, 6),
                'IC_std': round(ic_std, 6),
                'IC_IR': round(ic_ir, 4),
                'pos_ratio': round(pos_ratio, 4),
                't_stat': round(t_stat, 4),
            })

ic_df = pd.DataFrame(results)
ic_df.to_csv(IC_SUMMARY_PATH, index=False, encoding='utf-8-sig')

# ── 报告 ──────────────────────────────────────────

print(f"\n{'='*70}")
print(f"Rank IC 分析结果")
print(f"{'='*70}")

# IS, horizon=1d, 按 IC_IR 排序
is_1d = ic_df[(ic_df['period'] == 'IS') & (ic_df['horizon'] == 1)]
is_1d = is_1d.sort_values('IC_IR', ascending=False)

print(f"\n── IS 单因子 Rank IC (horizon=1d, 绝对值排序) ──")
print(f"{'Factor':<20s} {'N':>5s} {'IC Mean':>8s} {'IC Std':>8s} {'IC_IR':>8s} {'Pos%':>7s} {'t-stat':>7s}")
print(f"{'-'*20} {'-'*5} {'-'*8} {'-'*8} {'-'*8} {'-'*7} {'-'*7}")
for _, r in is_1d.iterrows():
    star = " ★" if abs(r['IC_IR']) >= 0.3 else ""
    print(f"{r['factor']:<20s} {int(r['N_days']):5d} {r['IC_mean']:8.4f} {r['IC_std']:8.4f} {r['IC_IR']:8.4f} {r['pos_ratio']:7.1%} {r['t_stat']:7.2f}{star}")

# IS vs OOS 对比
print(f"\n── IS vs OOS 稳定性 (horizon=1d) ──")
print(f"{'Factor':<20s} {'IS IC_IR':>9s} {'OOS IC_IR':>9s} {'OOS/IS':>7s} {'Status':>10s}")
print(f"{'-'*20} {'-'*9} {'-'*9} {'-'*7} {'-'*10}")
for fac in factor_cols:
    is_row = ic_df[(ic_df['factor'] == fac) & (ic_df['period'] == 'IS') & (ic_df['horizon'] == 1)]
    oos_row = ic_df[(ic_df['factor'] == fac) & (ic_df['period'] == 'OOS') & (ic_df['horizon'] == 1)]
    if len(is_row) == 0 or len(oos_row) == 0:
        continue
    is_ir = is_row.iloc[0]['IC_IR']
    oos_ir = oos_row.iloc[0]['IC_IR']
    ratio = oos_ir / is_ir if abs(is_ir) > 0.001 else float('nan')
    if np.isnan(ratio):
        status = "N/A"
    elif ratio >= 0.7:
        status = "✓ 稳定"
    elif ratio >= 0.5:
        status = "△ 衰减"
    else:
        status = "✗ 过拟合"
    print(f"{fac:<20s} {is_ir:9.4f} {oos_ir:9.4f} {ratio:7.2f} {status:>10s}")

# IC Decay (仅 IS)
print(f"\n── IC 衰减 (IS, 绝对值↓) ──")
print(f"{'Factor':<20s}", end="")
for h in FORWARD_PERIODS:
    print(f" {'h='+str(h)+'d':>9s}", end="")
print(f" {'Half-Life':>10s}")
print(f"{'-'*20}", end="")
for _ in FORWARD_PERIODS:
    print(f" {'-'*9}", end="")
print(f" {'-'*10}")

for fac in factor_cols:
    ics = []
    for h in FORWARD_PERIODS:
        row = ic_df[(ic_df['factor'] == fac) & (ic_df['period'] == 'IS') & (ic_df['horizon'] == h)]
        ics.append(abs(row.iloc[0]['IC_mean']) if len(row) > 0 else 0)
    # Half-life: first horizon where IC drops below 50% of h=1 IC
    hl = ">20d"
    if ics[0] > 0:
        for i, ic in enumerate(ics):
            if ic < ics[0] * 0.5:
                hl = f"{FORWARD_PERIODS[i]}d"
                break
    print(f"{fac:<20s}", end="")
    for ic in ics:
        print(f" {ic:9.4f}", end="")
    print(f" {hl:>10s}")

print(f"\n[SAVED] {IC_SUMMARY_PATH}")
print(f"\n★ IC_IR >= 0.3 的因子有预测能力")
print(f"★ OOS/IS >= 0.7 → 因子稳定, < 0.5 → 强烈过拟合")
print(f"\n下一步: python scripts/factor_research/factor_quantile.py")
