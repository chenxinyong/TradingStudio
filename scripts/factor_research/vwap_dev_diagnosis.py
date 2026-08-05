"""
VWAP_Dev Deep Diagnosis
=======================
Answers:
  1. Distribution analysis (skew/kurtosis, outliers)
  2. Extreme value analysis (top 10, clustering)
  3. IC stability over time (rolling 60d, cumulative IC)
  4. IC by instrument (which instruments drive IC, wrong-sign)
  5. LS portfolio failure (scatter, linearity check)
  6. Quantile return decomposition by instrument
  7. Conclusion: real factor or artifact?
"""

import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

import numpy as np
import pandas as pd
from scipy import stats
from datetime import datetime
import warnings
warnings.filterwarnings('ignore')

# Plotting
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import matplotlib.ticker as mticker
from matplotlib.gridspec import GridSpec

# ── Config ──────────────────────────────────────────
PANEL_PATH = "scripts/factor_research/output/factors_panel_v1.parquet"
OUTPUT_DIR = "scripts/factor_research/output/diagnosis"
os.makedirs(OUTPUT_DIR, exist_ok=True)

# Consistent styling
plt.rcParams.update({
    'figure.dpi': 150,
    'savefig.dpi': 150,
    'font.size': 9,
    'axes.titlesize': 11,
    'axes.labelsize': 10,
    'figure.facecolor': 'white',
    'axes.facecolor': '#f8f8f8',
})

# Color palette (brand-neutral, accessible)
BLUE   = '#2563EB'
RED    = '#DC2626'
GREEN  = '#16A34A'
ORANGE = '#EA580C'
PURPLE = '#7C3AED'
CYAN   = '#0891B2'
GRAY   = '#6B7280'
COLORS_5 = ['#DC2626', '#EA580C', '#F59E0B', '#16A34A', '#2563EB']  # Q1..Q5

# ── Load Data ────────────────────────────────────────
print("=" * 80)
print("VWAP_Dev Deep Diagnosis")
print("=" * 80)

panel = pd.read_parquet(PANEL_PATH)
panel['trading_day'] = pd.to_datetime(panel['trading_day'])
panel['bar_time'] = pd.to_datetime(panel['bar_time'])
panel = panel.sort_values(['instrument_id', 'trading_day']).reset_index(drop=True)

# Forward return: FwdRet_1d is already t→t+1
panel['daily_ret'] = panel.groupby('instrument_id')['FwdRet_1d'].shift(0)

# Drop rows where VWAP_Dev is NaN
vwap_data = panel.dropna(subset=['VWAP_Dev', 'daily_ret']).copy()
print(f"\nLoaded: {len(panel):,} rows, {panel['instrument_id'].nunique()} instruments")
print(f"After dropping VWAP_Dev NaN: {len(vwap_data):,} rows")
print(f"Date range: {vwap_data['trading_day'].min().date()} ~ {vwap_data['trading_day'].max().date()}")

# Split IS/OOS
is_mask = vwap_data['trading_day'] <= '2023-12-31'
oos_mask = vwap_data['trading_day'] > '2023-12-31'
vwap_is = vwap_data[is_mask]
vwap_oos = vwap_data[oos_mask]
print(f"IS: {len(vwap_is):,} | OOS: {len(vwap_oos):,}")

# ═══════════════════════════════════════════════════════════════
# Q1: DISTRIBUTION ANALYSIS
# ═══════════════════════════════════════════════════════════════
print("\n" + "=" * 80)
print("Q1: DISTRIBUTION ANALYSIS")
print("=" * 80)

vals = vwap_data['VWAP_Dev'].dropna().values
vals_is = vwap_is['VWAP_Dev'].dropna().values
vals_oos = vwap_oos['VWAP_Dev'].dropna().values

for label, v in [('ALL', vals), ('IS', vals_is), ('OOS', vals_oos)]:
    print(f"\n── {label} ──")
    print(f"  Count:     {len(v):,}")
    print(f"  Mean:      {np.mean(v):.6f}")
    print(f"  Median:    {np.median(v):.6f}")
    print(f"  Std:       {np.std(v):.6f}")
    print(f"  Skewness:  {stats.skew(v):.4f}")
    print(f"  Kurtosis:  {stats.kurtosis(v):.4f}  (excess, normal=0)")
    print(f"  Min:       {np.min(v):.6f}")
    print(f"  Max:       {np.max(v):.6f}")
    print(f"  P1:        {np.percentile(v, 1):.6f}")
    print(f"  P5:        {np.percentile(v, 5):.6f}")
    print(f"  P95:       {np.percentile(v, 95):.6f}")
    print(f"  P99:       {np.percentile(v, 99):.6f}")
    print(f"  P99.9:     {np.percentile(v, 99.9):.6f}")

    # Fat-tail check: P99.9/Std ratio (> 6 = very fat tailed)
    tail_ratio = np.abs(np.percentile(v, 99.9) / np.std(v)) if np.std(v) > 0 else 0
    print(f"  |P99.9|/Std: {tail_ratio:.1f}x  {'⚠ FAT TAILED' if tail_ratio > 6 else 'ok'}")

    # Normality test
    # Use subsample for Shapiro (max 5000)
    if len(v) > 5000:
        sample = np.random.RandomState(42).choice(v, 5000, replace=False)
    else:
        sample = v
    shapiro_stat, shapiro_p = stats.shapiro(sample)
    print(f"  Shapiro-Wilk W={shapiro_stat:.4f}, p={shapiro_p:.2e}  → {'NOT normal' if shapiro_p < 0.01 else 'Normal-like'}")

    # Jarque-Bera
    jb_stat, jb_p = stats.jarque_bera(sample)
    print(f"  Jarque-Bera: {jb_stat:.1f}, p={jb_p:.2e}  → {'NOT normal' if jb_p < 0.01 else 'Normal-like'}")

# ── Plot: Distribution ───────────────────────────────
fig, axes = plt.subplots(2, 2, figsize=(14, 10))

# 1) Histogram (full range + 1%/99% trimmed)
ax = axes[0, 0]
clip_lo, clip_hi = np.percentile(vals, 0.5), np.percentile(vals, 99.5)
v_trim = vals[(vals >= clip_lo) & (vals <= clip_hi)]
ax.hist(v_trim, bins=100, color=BLUE, alpha=0.8, edgecolor='white', linewidth=0.3)
ax.axvline(0, color=RED, linestyle='--', linewidth=1, alpha=0.6, label='zero')
ax.axvline(np.mean(vals), color=ORANGE, linestyle='-', linewidth=1, alpha=0.8, label=f'mean={np.mean(vals):.4f}')
ax.axvline(np.median(vals), color=GREEN, linestyle='-', linewidth=1, alpha=0.8, label=f'median={np.median(vals):.4f}')
ax.set_title(f'VWAP_Dev Distribution (trimmed 0.5%-99.5%)\nSkew={stats.skew(vals):.3f}, Kurt={stats.kurtosis(vals):.3f}')
ax.legend(fontsize=7, loc='upper right')
ax.set_xlabel('VWAP_Dev')
ax.set_ylabel('Frequency')

# 2) Q-Q plot
ax = axes[0, 1]
stats.probplot(v_trim, dist="norm", plot=ax)
ax.get_lines()[0].set_markerfacecolor(BLUE)
ax.get_lines()[0].set_markeredgecolor(BLUE)
ax.get_lines()[0].set_markersize(2)
ax.get_lines()[0].set_alpha(0.5)
ax.get_lines()[1].set_color(RED)
ax.set_title('Q-Q Plot (vs Normal)')
ax.set_xlabel('Theoretical Quantiles')
ax.set_ylabel('Sample Quantiles')

# 3) IS vs OOS overlaid KDE
ax = axes[1, 0]
for label, v, c in [('IS', vals_is, BLUE), ('OOS', vals_oos, ORANGE)]:
    clip_l, clip_h = np.percentile(v, 0.5), np.percentile(v, 99.5)
    v_t = v[(v >= clip_l) & (v <= clip_h)]
    density = stats.gaussian_kde(v_t)
    x = np.linspace(clip_l, clip_h, 500)
    ax.plot(x, density(x), color=c, linewidth=1.5, label=f'{label} (skew={stats.skew(v):.2f})')
ax.set_title('IS vs OOS Distribution (KDE)')
ax.legend(fontsize=7)
ax.set_xlabel('VWAP_Dev')
ax.set_ylabel('Density')

# 4) Tail behavior: log-log histogram of abs values
ax = axes[1, 1]
abs_vals = np.abs(vals)
abs_vals = abs_vals[abs_vals > 0]  # remove zeros
bins = np.logspace(np.log10(np.percentile(abs_vals, 10)), np.log10(np.percentile(abs_vals, 99.9)), 80)
ax.hist(abs_vals, bins=bins, color=BLUE, alpha=0.8, edgecolor='white', linewidth=0.2)
ax.set_xscale('log')
ax.set_yscale('log')
ax.set_title(f'Log-Log Tail: |VWAP_Dev|\nP99.9/Std={tail_ratio:.1f}x')
ax.set_xlabel('|VWAP_Dev| (log scale)')
ax.set_ylabel('Count (log scale)')
# Reference line: power law slope
x_ref = np.logspace(-3, 0, 100)
ax.plot(x_ref, 100 * x_ref**(-3), 'r--', alpha=0.3, label='~x^{-3}')
ax.legend(fontsize=7)

plt.tight_layout()
plt.savefig(os.path.join(OUTPUT_DIR, 'q1_distribution.png'), dpi=150, bbox_inches='tight')
plt.close()
print(f"\n[Chart saved] q1_distribution.png")

# ═══════════════════════════════════════════════════════════════
# Q2: EXTREME VALUE ANALYSIS
# ═══════════════════════════════════════════════════════════════
print("\n" + "=" * 80)
print("Q2: EXTREME VALUE ANALYSIS")
print("=" * 80)

# Top 10 most extreme (by absolute value)
vwap_data['abs_vwap'] = np.abs(vwap_data['VWAP_Dev'])
top10 = vwap_data.nlargest(10, 'abs_vwap')[
    ['instrument_id', 'trading_day', 'VWAP_Dev', 'daily_ret', 'period']
]
print("\n── Top 10 Most Extreme |VWAP_Dev| ──")
print(f"{'Instrument':<12s} {'Date':>12s} {'VWAP_Dev':>12s} {'FwdRet':>10s} {'Period':>6s}")
print(f"{'-'*12} {'-'*12} {'-'*12} {'-'*10} {'-'*6}")
for _, r in top10.iterrows():
    print(f"{r['instrument_id']:<12s} {str(r['trading_day'].date()):>12s} {r['VWAP_Dev']:12.6f} {r['daily_ret']:10.4%} {r['period']:>6s}")

# Top 10 negative extremes
top10_neg = vwap_data.nsmallest(10, 'VWAP_Dev')[
    ['instrument_id', 'trading_day', 'VWAP_Dev', 'daily_ret', 'period']
]
print(f"\n── Top 10 Most Negative VWAP_Dev (close << VWAP) ──")
for _, r in top10_neg.iterrows():
    print(f"{r['instrument_id']:<12s} {str(r['trading_day'].date()):>12s} {r['VWAP_Dev']:12.6f} {r['daily_ret']:10.4%} {r['period']:>6s}")

# Top 10 positive extremes
top10_pos = vwap_data.nlargest(10, 'VWAP_Dev')[
    ['instrument_id', 'trading_day', 'VWAP_Dev', 'daily_ret', 'period']
]
print(f"\n── Top 10 Most Positive VWAP_Dev (close >> VWAP) ──")
for _, r in top10_pos.iterrows():
    print(f"{r['instrument_id']:<12s} {str(r['trading_day'].date()):>12s} {r['VWAP_Dev']:12.6f} {r['daily_ret']:10.4%} {r['period']:>6s}")

# Clustering: how many instruments contribute to top 1% extremes?
top1pct_threshold = np.percentile(np.abs(vwap_data['VWAP_Dev']), 99)
extreme_rows = vwap_data[np.abs(vwap_data['VWAP_Dev']) >= top1pct_threshold]
print(f"\n── Extreme Concentration (top 1% threshold = |VWAP_Dev| >= {top1pct_threshold:.4f}) ──")
print(f"  Top 1% extreme rows: {len(extreme_rows):,} / {len(vwap_data):,}")

inst_counts = extreme_rows['instrument_id'].value_counts()
print(f"\n  Top 10 instruments generating extremes:")
for inst, cnt in inst_counts.head(10).items():
    pct = cnt / len(extreme_rows) * 100
    print(f"    {inst:<12s} {cnt:5d}  ({pct:5.1f}%)")

# Time clustering: extreme count by month
extreme_rows['year_month'] = extreme_rows['trading_day'].dt.to_period('M')
monthly_extremes = extreme_rows.groupby('year_month').size()
print(f"\n  Top 10 months for extreme clustering:")
for (ym, cnt) in monthly_extremes.nlargest(10).items():
    print(f"    {ym}  {cnt:4d} extremes")

# ── Plot: Extreme values over time ───────────────────
fig, axes = plt.subplots(2, 1, figsize=(14, 8))

# 1) VWAP_Dev time series (sample: daily median + IQR)
ax = axes[0]
daily_stats = vwap_data.groupby('trading_day')['VWAP_Dev'].agg(['mean', 'std',
    lambda x: np.percentile(x, 10), lambda x: np.percentile(x, 90)])
daily_stats.columns = ['mean', 'std', 'p10', 'p90']
days_dt = daily_stats.index.to_list()

ax.fill_between(days_dt, daily_stats['p10'], daily_stats['p90'],
                alpha=0.2, color=BLUE, label='P10-P90')
ax.plot(days_dt, daily_stats['mean'], color=BLUE, linewidth=0.5, alpha=0.7, label='Cross-sectional mean')
ax.axhline(0, color=RED, linestyle='--', linewidth=0.8, alpha=0.5)
ax.set_title('VWAP_Dev Cross-Sectional Distribution Over Time')
ax.set_ylabel('VWAP_Dev')
ax.legend(fontsize=7)

# 2) Monthly extreme count
ax = axes[1]
monthly_idx = monthly_extremes.index.to_timestamp()
ax.bar(monthly_idx, monthly_extremes.values, color=ORANGE, alpha=0.8, width=20)
ax.set_title('Monthly Count of Extreme VWAP_Dev (top 1%)')
ax.set_ylabel('Count')
ax.axhline(monthly_extremes.median(), color=RED, linestyle='--', linewidth=0.8,
           alpha=0.5, label=f'median={monthly_extremes.median():.0f}')
ax.legend(fontsize=7)

plt.tight_layout()
plt.savefig(os.path.join(OUTPUT_DIR, 'q2_extremes.png'), dpi=150, bbox_inches='tight')
plt.close()
print(f"\n[Chart saved] q2_extremes.png")

# ═══════════════════════════════════════════════════════════════
# Q3: IC STABILITY OVER TIME
# ═══════════════════════════════════════════════════════════════
print("\n" + "=" * 80)
print("Q3: IC STABILITY OVER TIME")
print("=" * 80)

# Compute daily Rank IC
def daily_ic(df):
    """Spearman rank correlation: VWAP_Dev vs daily_ret per trading day"""
    valid = df.dropna(subset=['VWAP_Dev', 'daily_ret'])
    if len(valid) < 5:
        return np.nan
    return stats.spearmanr(valid['VWAP_Dev'].values, valid['daily_ret'].values)[0]

ic_series = vwap_data.groupby('trading_day').apply(daily_ic).dropna()
ic_series.name = 'IC'

print(f"\n── Full-sample Daily Rank IC ──")
print(f"  N days:     {len(ic_series)}")
print(f"  Mean IC:    {ic_series.mean():.6f}")
print(f"  Std IC:     {ic_series.std():.6f}")
print(f"  IC_IR:      {ic_series.mean() / ic_series.std():.4f}")
print(f"  Pos ratio:  {(ic_series > 0).mean():.1%}")
print(f"  t-stat:     {ic_series.mean() / (ic_series.std() / np.sqrt(len(ic_series))):.2f}")

# IS/OOS split
ic_is = ic_series[ic_series.index <= '2023-12-31']
ic_oos = ic_series[ic_series.index > '2023-12-31']
for label, ic in [('IS', ic_is), ('OOS', ic_oos)]:
    if len(ic) == 0:
        continue
    print(f"\n  {label}:")
    print(f"    N={len(ic)}, Mean={ic.mean():.6f}, Std={ic.std():.6f}, IC_IR={ic.mean()/ic.std():.4f}, Pos={ (ic>0).mean():.1%}")

# Rolling 60-day IC
rolling_ic = ic_series.rolling(60, min_periods=30).mean()
rolling_std = ic_series.rolling(60, min_periods=30).std()
print(f"\n── Rolling 60-day IC ──")
print(f"  Min rolling IC:   {rolling_ic.min():.4f}  (date: {rolling_ic.idxmin().date()})")
print(f"  Max rolling IC:   {rolling_ic.max():.4f}  (date: {rolling_ic.idxmax().date()})")
print(f"  % days IC > 0:    {(rolling_ic > 0).mean():.1%}")
print(f"  % days IC < 0:    {(rolling_ic < 0).mean():.1%}")

# Find worst positive-IC and worst negative-IC periods
pos_peak = rolling_ic.nlargest(3)
neg_peak = rolling_ic.nsmallest(3)
print(f"\n  Top 3 positive rolling IC periods:")
for dt, val in pos_peak.items():
    print(f"    {dt.date()}  IC={val:.4f}")
print(f"  Top 3 negative rolling IC periods:")
for dt, val in neg_peak.items():
    print(f"    {dt.date()}  IC={val:.4f}")

# Cumulative IC
cum_ic = ic_series.cumsum()
print(f"\n── Cumulative IC ──")
print(f"  Start: {cum_ic.iloc[0]:.4f}  ({ic_series.index[0].date()})")
print(f"  End:   {cum_ic.iloc[-1]:.4f}  ({ic_series.index[-1].date()})")

# ── Plot: IC over time ───────────────────────────────
fig, axes = plt.subplots(3, 1, figsize=(14, 12), sharex=True)

# 1) Daily IC scatter
ax = axes[0]
ic_dates = ic_series.index.to_list()
colors = [RED if v < 0 else GREEN for v in ic_series.values]
ax.bar(ic_dates, ic_series.values, color=colors, alpha=0.5, width=1)
ax.axhline(0, color='black', linewidth=0.5)
ax.axhline(ic_series.mean(), color=BLUE, linestyle='--', linewidth=1, label=f'mean={ic_series.mean():.4f}')
ax.set_title(f'Daily Rank IC: VWAP_Dev vs FwdRet_1d\nIC_IR={ic_series.mean()/ic_series.std():.3f}, Pos={(ic_series>0).mean():.1%}')
ax.set_ylabel('Rank IC')
ax.legend(fontsize=7)

# 2) Rolling 60-day IC
ax = axes[1]
ax.plot(rolling_ic.index, rolling_ic.values, color=BLUE, linewidth=1)
ax.fill_between(rolling_ic.index,
                (rolling_ic - 2 * rolling_std / np.sqrt(60)).values,
                (rolling_ic + 2 * rolling_std / np.sqrt(60)).values,
                alpha=0.15, color=BLUE)
ax.axhline(0, color='black', linewidth=0.5)
ax.set_title(f'Rolling 60-Day Mean IC\nMin={rolling_ic.min():.4f}, Max={rolling_ic.max():.4f}')
ax.set_ylabel('Rolling Mean IC')

# 3) Cumulative IC
ax = axes[2]
ax.plot(cum_ic.index, cum_ic.values, color=PURPLE, linewidth=1)
ax.fill_between(cum_ic.index, 0, cum_ic.values,
                where=(cum_ic.values >= 0), color=GREEN, alpha=0.2)
ax.fill_between(cum_ic.index, 0, cum_ic.values,
                where=(cum_ic.values < 0), color=RED, alpha=0.2)
ax.axhline(0, color='black', linewidth=0.5)
ax.set_title(f'Cumulative Rank IC\nEnd value = {cum_ic.iloc[-1]:.2f}')
ax.set_xlabel('Date')
ax.set_ylabel('Cumulative IC')
# Add IS/OOS divider
ax.axvline(pd.Timestamp('2024-01-01'), color=GRAY, linestyle=':', linewidth=1, alpha=0.7)
ax.text(pd.Timestamp('2024-01-15'), cum_ic.iloc[-1] * 0.9, 'OOS→', fontsize=7, color=GRAY)

plt.tight_layout()
plt.savefig(os.path.join(OUTPUT_DIR, 'q3_ic_stability.png'), dpi=150, bbox_inches='tight')
plt.close()
print(f"\n[Chart saved] q3_ic_stability.png")

# ═══════════════════════════════════════════════════════════════
# Q4: IC BY INSTRUMENT
# ═══════════════════════════════════════════════════════════════
print("\n" + "=" * 80)
print("Q4: IC BY INSTRUMENT")
print("=" * 80)

# Per-instrument IC (time-series within each instrument)
def instrument_ic(grp):
    valid = grp.dropna(subset=['VWAP_Dev', 'daily_ret'])
    if len(valid) < 30:
        return pd.Series({'N': len(valid), 'IC': np.nan, 'IC_tstat': np.nan})
    ic, _ = stats.spearmanr(valid['VWAP_Dev'].values, valid['daily_ret'].values)
    n = len(valid)
    tstat = ic * np.sqrt((n - 2) / (1 - ic**2)) if abs(ic) < 1 else np.inf
    return pd.Series({'N': n, 'IC': ic, 'IC_tstat': tstat})

inst_ic = vwap_data.groupby('instrument_id').apply(instrument_ic).reset_index()
inst_ic = inst_ic.dropna(subset=['IC']).sort_values('IC')

print(f"\n── IC by Instrument (sorted, most negative first) ──")
print(f"{'Instrument':<12s} {'N':>6s} {'IC':>10s} {'t-stat':>8s} {'Signal':>10s}")
print(f"{'-'*12} {'-'*6} {'-'*10} {'-'*8} {'-'*10}")
for _, r in inst_ic.iterrows():
    signal = "WRONG (+)" if r['IC'] > 0 else "CORRECT (-)"
    marker = " ⚠" if r['IC'] > 0 else ""
    print(f"{r['instrument_id']:<12s} {int(r['N']):6d} {r['IC']:10.4f} {r['IC_tstat']:8.2f} {signal:>10s}{marker}")

n_wrong = (inst_ic['IC'] > 0).sum()
n_total = len(inst_ic)
print(f"\n  Instruments with WRONG sign (positive IC): {n_wrong}/{n_total}")
print(f"  % wrong sign: {n_wrong/n_total:.1%}")

# Weighted contribution: instrument IC * sqrt(N_days) ≈ contribution to overall IC
inst_ic['weight'] = np.sqrt(inst_ic['N'])
inst_ic['weighted_IC'] = inst_ic['IC'] * inst_ic['weight'] / inst_ic['weight'].sum()
print(f"\n  Top 5 positive contributors (driving IC toward WRONG sign):")
for _, r in inst_ic.nlargest(5, 'weighted_IC').iterrows():
    print(f"    {r['instrument_id']:<12s} weighted_IC={r['weighted_IC']:.6f}, IC={r['IC']:.4f}, N={int(r['N'])}")

print(f"\n  Top 5 negative contributors (driving IC toward CORRECT sign):")
for _, r in inst_ic.nsmallest(5, 'weighted_IC').iterrows():
    print(f"    {r['instrument_id']:<12s} weighted_IC={r['weighted_IC']:.6f}, IC={r['IC']:.4f}, N={int(r['N'])}")

# ── Plot: IC by instrument ───────────────────────────
fig, ax = plt.subplots(figsize=(14, 8))
inst_names = inst_ic['instrument_id'].values
inst_ics = inst_ic['IC'].values
inst_n = inst_ic['N'].values
colors = [RED if ic > 0 else BLUE for ic in inst_ics]
bars = ax.barh(range(len(inst_names)), inst_ics, color=colors, alpha=0.8, height=0.7)
ax.set_yticks(range(len(inst_names)))
ax.set_yticklabels(inst_names, fontsize=7)
ax.axvline(0, color='black', linewidth=0.7)
ax.axvline(ic_series.mean(), color=GRAY, linestyle='--', linewidth=0.8, alpha=0.5, label=f'Overall mean IC={ic_series.mean():.4f}')
ax.set_title(f'VWAP_Dev IC by Instrument\n{n_wrong}/{n_total} instruments have WRONG sign (positive IC)')
ax.set_xlabel('Instrument-level Rank IC')
ax.legend(fontsize=7)

# Add N annotation
for i, (ic_val, n_val) in enumerate(zip(inst_ics, inst_n)):
    ax.text(ic_val + (0.002 if ic_val >= 0 else -0.002), i,
            f'N={int(n_val)}', fontsize=5, va='center',
            ha='left' if ic_val >= 0 else 'right', color=GRAY)

plt.tight_layout()
plt.savefig(os.path.join(OUTPUT_DIR, 'q4_ic_by_instrument.png'), dpi=150, bbox_inches='tight')
plt.close()
print(f"\n[Chart saved] q4_ic_by_instrument.png")

# ═══════════════════════════════════════════════════════════════
# Q5: LS PORTFOLIO FAILURE ANALYSIS
# ═══════════════════════════════════════════════════════════════
print("\n" + "=" * 80)
print("Q5: LS PORTFOLIO FAILURE ANALYSIS — Scatter & Linearity Check")
print("=" * 80)

# Use OOS data (where LS Sharpe was computed as -4.616)
vwap_oos_clean = vwap_oos.dropna(subset=['VWAP_Dev', 'daily_ret'])

# 1) Scatter: VWAP_Dev vs forward return (sample)
n_plot = min(20000, len(vwap_oos_clean))
sample_oos = vwap_oos_clean.sample(n_plot, random_state=42)

# 2) Quantile-based non-parametric relationship
n_buckets = 50
vwap_oos_clean['vwap_bucket'] = pd.qcut(vwap_oos_clean['VWAP_Dev'].rank(method='first'),
                                          n_buckets, labels=False, duplicates='drop')
bucket_stats = vwap_oos_clean.groupby('vwap_bucket').agg(
    vwap_mean=('VWAP_Dev', 'mean'),
    fwd_ret_mean=('daily_ret', 'mean'),
    fwd_ret_std=('daily_ret', 'std'),
    count=('daily_ret', 'count')
).reset_index()

# 3) Fit linear regression
from sklearn.linear_model import LinearRegression
lr = LinearRegression()
X = vwap_oos_clean['VWAP_Dev'].values.reshape(-1, 1)
y = vwap_oos_clean['daily_ret'].values
lr.fit(X, y)
print(f"\n── Linear Regression: FwdRet ~ VWAP_Dev (OOS) ──")
print(f"  Slope:      {lr.coef_[0]:.6f}")
print(f"  Intercept:  {lr.intercept_:.6f}")
print(f"  R²:         {lr.score(X, y):.6f}")
print(f"  Implied:    1% VWAP_Dev → {lr.coef_[0]*0.01:.4%} forward return")

# 4) Piecewise: check if extreme values drive it
# Split data into middle 90% and extreme 5% tails
p5_lo, p5_hi = np.percentile(vwap_oos_clean['VWAP_Dev'], 5), np.percentile(vwap_oos_clean['VWAP_Dev'], 95)
middle = vwap_oos_clean[(vwap_oos_clean['VWAP_Dev'] >= p5_lo) & (vwap_oos_clean['VWAP_Dev'] <= p5_hi)]
low_tail = vwap_oos_clean[vwap_oos_clean['VWAP_Dev'] < p5_lo]
high_tail = vwap_oos_clean[vwap_oos_clean['VWAP_Dev'] > p5_hi]

print(f"\n── Piecewise Analysis (OOS) ──")
for label, subset in [('Bottom 5% tail', low_tail), ('Middle 90%', middle), ('Top 5% tail', high_tail)]:
    if len(subset) < 30:
        continue
    ic, _ = stats.spearmanr(subset['VWAP_Dev'].values, subset['daily_ret'].values)
    # Simple linear slope
    Xs = subset['VWAP_Dev'].values.reshape(-1, 1)
    ys = subset['daily_ret'].values
    lr_seg = LinearRegression()
    lr_seg.fit(Xs, ys)
    print(f"  {label}: N={len(subset):,}, IC={ic:.4f}, slope={lr_seg.coef_[0]:.6f}, R²={lr_seg.score(Xs, ys):.6f}")

# 5) Bootstrap IC from extremes vs middle
print(f"\n── Bootstrap: Remove extreme 1% tails → recalculate IC ──")
p1_lo, p1_hi = np.percentile(vwap_oos_clean['VWAP_Dev'], 1), np.percentile(vwap_oos_clean['VWAP_Dev'], 99)
trimmed = vwap_oos_clean[(vwap_oos_clean['VWAP_Dev'] >= p1_lo) & (vwap_oos_clean['VWAP_Dev'] <= p1_hi)]
trimmed_ic_daily = trimmed.groupby('trading_day').apply(daily_ic).dropna()
print(f"  Full OOS IC:  {ic_oos.mean():.4f}")
print(f"  Trimmed (1%) IC: {trimmed_ic_daily.mean():.4f}  (N_days={len(trimmed_ic_daily)})")

# Winsorize at 1%
vwap_oos_clean['VWAP_Dev_w1'] = stats.mstats.winsorize(vwap_oos_clean['VWAP_Dev'], limits=(0.01, 0.01))
winsor_ic_daily = vwap_oos_clean.dropna(subset=['VWAP_Dev_w1', 'daily_ret']).groupby('trading_day').apply(
    lambda g: stats.spearmanr(g['VWAP_Dev_w1'].values, g['daily_ret'].values)[0] if len(g.dropna(subset=['VWAP_Dev_w1', 'daily_ret'])) >= 5 else np.nan
).dropna()
print(f"  Winsorized (1%) IC: {winsor_ic_daily.mean():.4f}  (N_days={len(winsor_ic_daily)})")

# ── Plot: Scatter + bucket regression ────────────────
fig, axes = plt.subplots(2, 2, figsize=(14, 12))

# 1) Scatter sample
ax = axes[0, 0]
ax.scatter(sample_oos['VWAP_Dev'], sample_oos['daily_ret'],
           alpha=0.15, s=3, color=BLUE, edgecolors='none')
# Regression line
x_line = np.linspace(vwap_oos_clean['VWAP_Dev'].min(), vwap_oos_clean['VWAP_Dev'].max(), 200)
ax.plot(x_line, lr.predict(x_line.reshape(-1, 1)), color=RED, linewidth=2,
        label=f'y={lr.coef_[0]:.4f}x+{lr.intercept_:.4f}, R²={lr.score(X,y):.4f}')
ax.axhline(0, color='black', linewidth=0.5)
ax.axvline(0, color='black', linewidth=0.5)
# Mark tail boundaries
for pct, ls in [(5, '--'), (95, '--')]:
    ax.axvline(np.percentile(vwap_oos_clean['VWAP_Dev'], pct), color=ORANGE,
               linestyle=ls, linewidth=0.8, alpha=0.5)
ax.set_title(f'OOS: VWAP_Dev vs FwdRet_1d (sample n={n_plot:,})')
ax.set_xlabel('VWAP_Dev')
ax.set_ylabel('Forward Return (1d)')
ax.legend(fontsize=7)

# 2) Bucket (non-parametric) relationship
ax = axes[1, 0]
ax.errorbar(bucket_stats['vwap_mean'], bucket_stats['fwd_ret_mean'],
            yerr=bucket_stats['fwd_ret_std'] / np.sqrt(bucket_stats['count']),
            fmt='o-', color=BLUE, markersize=3, linewidth=1, alpha=0.7,
            elinewidth=0.5, capsize=2)
ax.axhline(0, color='black', linewidth=0.5)
ax.axvline(0, color='black', linewidth=0.5)
ax.set_title(f'Non-parametric: {n_buckets}-Bucket Mean Relationship\n')
ax.set_xlabel('VWAP_Dev (bucket mean)')
ax.set_ylabel('Mean Forward Return')

# 3) Cumulative return by VWAP_Dev sign (验证方向)
ax = axes[1, 1]
# Assign sign based on each day's VWAP_Dev quantile ranking
oos_daily = vwap_oos_clean.copy()
oos_daily['q_rank'] = oos_daily.groupby('trading_day')['VWAP_Dev'].transform(
    lambda x: x.rank(pct=True))
oos_daily['bucket'] = pd.cut(oos_daily['q_rank'], [0, 0.2, 0.4, 0.6, 0.8, 1.0],
                              labels=['Q1 (Low)', 'Q2', 'Q3', 'Q4', 'Q5 (High)'])
daily_q = oos_daily.groupby(['trading_day', 'bucket'])['daily_ret'].mean().unstack()
cum_q = (1 + daily_q.fillna(0)).cumprod()

for q, c in zip(cum_q.columns, COLORS_5):
    ax.plot(cum_q.index, cum_q[q], color=c, linewidth=1, label=q)
ax.set_title('OOS Cumulative Return by VWAP_Dev Quantile')
ax.set_xlabel('Date')
ax.set_ylabel('Cumulative Return')
ax.legend(fontsize=7)
ax.yaxis.set_major_formatter(mticker.FuncFormatter(lambda y, _: f'{y:.2f}'))

# 4) IC comparison: full vs trimmed vs winsorized
ax = axes[0, 1]
categories = ['Full OOS', 'Trim 1%', 'Winsor 1%']
values = [ic_oos.mean(), trimmed_ic_daily.mean(), winsor_ic_daily.mean()]
std_vals = [ic_oos.std() / np.sqrt(len(ic_oos)),
            trimmed_ic_daily.std() / np.sqrt(len(trimmed_ic_daily)),
            winsor_ic_daily.std() / np.sqrt(len(winsor_ic_daily))]
bars = ax.bar(categories, values, color=[BLUE, ORANGE, GREEN], alpha=0.8, width=0.5)
ax.errorbar(categories, values, yerr=2*np.array(std_vals), fmt='none',
            color='black', capsize=8, linewidth=1.5)
ax.axhline(0, color='black', linewidth=0.5)
ax.set_title('IC Stability to Outlier Treatment (OOS)')
for bar, val in zip(bars, values):
    ax.text(bar.get_x() + bar.get_width()/2, val + (0.002 if val > 0 else -0.002),
            f'{val:.4f}', ha='center', va='bottom' if val > 0 else 'top', fontsize=10, fontweight='bold')

plt.tight_layout()
plt.savefig(os.path.join(OUTPUT_DIR, 'q5_ls_failure.png'), dpi=150, bbox_inches='tight')
plt.close()
print(f"\n[Chart saved] q5_ls_failure.png")

# ═══════════════════════════════════════════════════════════════
# Q6: QUANTILE RETURN DECOMPOSITION BY INSTRUMENT
# ═══════════════════════════════════════════════════════════════
print("\n" + "=" * 80)
print("Q6: QUANTILE RETURN DECOMPOSITION BY INSTRUMENT")
print("=" * 80)

# Assign quantiles daily in OOS
oos_daily = vwap_oos_clean.copy()
oos_daily['quantile'] = oos_daily.groupby('trading_day')['VWAP_Dev'].transform(
    lambda x: pd.qcut(x.rank(method='first'), 5, labels=False, duplicates='drop')
)
oos_daily = oos_daily.dropna(subset=['quantile'])
oos_daily['quantile'] = oos_daily['quantile'].astype(int)

print(f"OOS quantile assignments: {len(oos_daily):,} rows")

# Per quantile, per instrument: average return
q_inst = oos_daily.groupby(['quantile', 'instrument_id'])['daily_ret'].agg(['mean', 'count', 'std']).reset_index()
q_inst['ann_ret'] = (1 + q_inst['mean']) ** 242 - 1

print(f"\n── Q1 (Most Negative VWAP_Dev): Top 10 instruments by return ──")
q1 = q_inst[q_inst['quantile'] == 0].nlargest(10, 'ann_ret')
for _, r in q1.iterrows():
    print(f"  {r['instrument_id']:<12s} ann_ret={r['ann_ret']:8.2%}, count={int(r['count']):5d}, mean_daily={r['mean']:.4%}")

print(f"\n── Q5 (Most Positive VWAP_Dev): Top 10 instruments by return ──")
q5 = q_inst[q_inst['quantile'] == 4].nlargest(10, 'ann_ret')
for _, r in q5.iterrows():
    print(f"  {r['instrument_id']:<12s} ann_ret={r['ann_ret']:8.2%}, count={int(r['count']):5d}, mean_daily={r['mean']:.4%}")

# Dominance check: what % of Q1/Q5 return comes from top 3 instruments?
for q_num, q_label in [(0, 'Q1 (low VWAP_Dev, expected HIGH return)'), (4, 'Q5 (high VWAP_Dev, expected LOW return)')]:
    q_data = oos_daily[oos_daily['quantile'] == q_num]
    # Overall Q return
    q_daily = q_data.groupby('trading_day')['daily_ret'].mean()
    q_total_ret = (1 + q_daily).prod() - 1

    # Per-instrument contribution
    inst_contrib = q_data.groupby('instrument_id')['daily_ret'].mean().sort_values(ascending=False)
    top3 = inst_contrib.head(3)
    total_inst_mean = inst_contrib.abs().sum()
    top3_pct = top3.abs().sum() / total_inst_mean * 100

    print(f"\n── {q_label} ──")
    print(f"  Total Q return: {q_total_ret:.2%}")
    print(f"  Top 3 instruments dominate: {top3_pct:.0f}% of cross-sectional mean")
    for inst, ret in top3.items():
        n = len(q_data[q_data['instrument_id'] == inst])
        print(f"    {inst:<12s} daily_mean={ret:.4%}, N={n}")

# ── Plot: Quantile return by instrument horizon ──────
fig, axes = plt.subplots(2, 1, figsize=(14, 12))

# 1) Heatmap: instrument x quantile return
pivot = q_inst.pivot_table(values='ann_ret', index='instrument_id', columns='quantile', aggfunc='mean')
pivot = pivot.loc[pivot.notna().all(axis=1)]  # only instruments present in all quantiles

ax = axes[0]
im = ax.imshow(pivot.values, cmap='RdBu_r', aspect='auto', vmin=-0.5, vmax=0.5,
               interpolation='nearest')
ax.set_xticks(range(5))
ax.set_xticklabels([f'Q{i+1}' for i in range(5)])
ax.set_yticks(range(len(pivot.index)))
ax.set_yticklabels(pivot.index, fontsize=7)
ax.set_title('Annualized Return by Instrument x Quantile (OOS)\nRed=Negative, Blue=Positive')
plt.colorbar(im, ax=ax, label='Ann. Return', shrink=0.8)

# 2) Bar chart: Q5-Q1 spread per instrument
ax = axes[1]
spread = (pivot[4] - pivot[0]).sort_values()  # Q5 - Q1 (want negative for correct signal)
colors_spread = [RED if s > 0 else GREEN for s in spread.values]
ax.barh(range(len(spread)), spread.values, color=colors_spread, alpha=0.8, height=0.7)
ax.set_yticks(range(len(spread)))
ax.set_yticklabels(spread.index, fontsize=7)
ax.axvline(0, color='black', linewidth=0.7)
ax.set_title(f'Q5-Q1 Return Spread by Instrument\nGreen=Correct sign (Q5<Q1), Red=Wrong sign (Q5>Q1)')
ax.set_xlabel('Q5 Annualized Return - Q1 Annualized Return')

plt.tight_layout()
plt.savefig(os.path.join(OUTPUT_DIR, 'q6_quantile_decomposition.png'), dpi=150, bbox_inches='tight')
plt.close()
print(f"\n[Chart saved] q6_quantile_decomposition.png")

# ═══════════════════════════════════════════════════════════════
# Q7: CONCLUSION
# ═══════════════════════════════════════════════════════════════
print("\n" + "=" * 80)
print("Q7: CONCLUSION")
print("=" * 80)

# Compute summary statistics
skew_all = stats.skew(vals)
kurt_all = stats.kurtosis(vals)
tail_ratio = np.abs(np.percentile(vals, 99.9) / np.std(vals))
n_wrong_instruments = (inst_ic['IC'] > 0).sum()
n_total_instruments = len(inst_ic)

# IC decay after trimming
ic_full_mean = ic_oos.mean()
ic_trim_mean = trimmed_ic_daily.mean()
ic_decay = (ic_full_mean - ic_trim_mean) / abs(ic_full_mean) * 100

print(f"""
VWAP_Dev Deep Diagnosis — Summary
{'─' * 60}

1. DISTRIBUTION:
   Skew={skew_all:.3f}, Kurtosis={kurt_all:.3f}, |P99.9|/Std={tail_ratio:.1f}x
   → VWAP_Dev is {'strongly' if abs(skew_all) > 1 else 'moderately'} {'left' if skew_all < 0 else 'right'}-skewed
   → {'HEAVILY fat-tailed (leptokurtic) — extreme outliers are common' if kurt_all > 3 else 'Moderately fat-tailed' if kurt_all > 1 else 'Near normal'}

2. EXTREME VALUES:
   Top 1% threshold = |VWAP_Dev| >= {top1pct_threshold:.4f}
   Top 10 extremes span {top10['VWAP_Dev'].abs().min():.4f} to {top10['VWAP_Dev'].abs().max():.4f}

3. IC STABILITY:
   Full-sample IC = {ic_series.mean():.4f} (daily Rank IC, Spearman)
   OOS IC = {ic_oos.mean():.4f}
   Rolling 60d IC range: [{rolling_ic.min():.4f}, {rolling_ic.max():.4f}]
   → IC is {'STABLE' if rolling_ic.std() < 0.03 else 'MODERATELY volatile' if rolling_ic.std() < 0.05 else 'HIGHLY volatile'}

4. INSTRUMENT CONTRIBUTION:
   {n_wrong_instruments}/{n_total_instruments} instruments have WRONG sign (positive IC)
   → {'Many instruments contradict the factor hypothesis — VWAP_Dev not universal' if n_wrong_instruments > n_total_instruments*0.3 else 'Most instruments align with the factor hypothesis'}

5. LS FAILURE:
   R² of linear fit = {lr.score(X, y):.6f}
   IC after trimming 1% tails: {ic_full_mean:.4f} → {ic_trim_mean:.4f} ({'DECAY' if abs(ic_trim_mean) < abs(ic_full_mean) else 'IMPROVES'} by {abs(ic_decay):.0f}%)
   Bucket relationship is {'roughly linear' if bucket_stats['fwd_ret_mean'].corr(bucket_stats['vwap_mean']) > 0.8 else 'NOISY — middle range flat, extremes drive the IC'}

6. QUANTILE DECOMPOSITION:
   Q1 instruments: {', '.join(q1['instrument_id'].head(3).values)}
   Q5 instruments: {', '.join(q5['instrument_id'].head(3).values)}
   → {'Specific instruments dominate extreme quantiles — NOT a broad cross-sectional effect' if top3_pct > 40 else 'Returns are well-distributed across instruments in each quantile'}

7. VERDICT:
   {'REAL FACTOR — stable, broad-based, survives outlier treatment' if abs(ic_trim_mean) > 0.01 and n_wrong_instruments < n_total_instruments*0.3 else 'ARTIFACT / UNRELIABLE — driven by extreme values, instrument-specific, or decays after outlier treatment' if abs(ic_decay) > 50 or n_wrong_instruments > n_total_instruments*0.3 else 'MARGINAL — has some signal but needs refinement (outlier handling, instrument selection)'}
""")

print(f"\n[Charts saved to] {OUTPUT_DIR}/")
print(f"  q1_distribution.png")
print(f"  q2_extremes.png")
print(f"  q3_ic_stability.png")
print(f"  q4_ic_by_instrument.png")
print(f"  q5_ls_failure.png")
print(f"  q6_quantile_decomposition.png")
print(f"\nDone.")
