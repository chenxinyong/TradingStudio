"""
Factor Mining Phase 1: 分组回测 (Quantile Portfolio)
每天按因子值分5组, 等权持仓, 评估多空收益差

用法: cd TradingStudio && python scripts/factor_research/factor_quantile.py

输入: output/factors_panel_v1_clean.parquet
输出: output/quantile_results.csv + 终端报告
"""

import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

import numpy as np
import pandas as pd
import warnings
warnings.filterwarnings('ignore')

# ── 配置 ──────────────────────────────────────────

OUTPUT_DIR = "scripts/factor_research/output"
INPUT_PATH = os.path.join(OUTPUT_DIR, "factors_panel_v1_clean.parquet")
RESULT_PATH = os.path.join(OUTPUT_DIR, "quantile_results.csv")

N_QUANTILES = 5
MIN_SAMPLES_PER_DAY = 5

print("=" * 70)
print(f"Quantile Portfolio: {N_QUANTILES}-Group Analysis")
print("=" * 70)

# ── 加载 ──────────────────────────────────────────

panel = pd.read_parquet(INPUT_PATH)
panel['bar_time'] = pd.to_datetime(panel['bar_time'])
panel = panel.sort_values(['bar_time', 'instrument_id']).reset_index(drop=True)

factor_cols = [c for c in panel.columns if c not in
    ['instrument_id', 'bar_time', 'period', 'FwdRet_1d']]

# 日收益 (从 FwdRet_1d 取)
panel['daily_ret'] = panel.groupby('instrument_id')['FwdRet_1d'].shift(0)

# ── 分组回测函数 ──────────────────────────────────

def quantile_backtest(panel: pd.DataFrame, factor_name: str, period_label: str = 'ALL'):
    """按因子值每天分5组, 计算等权组合收益"""

    df = panel.dropna(subset=[factor_name, 'daily_ret']).copy()
    if len(df) < 100:
        return None

    # 每天按因子值排序, 打分组标签
    df['quantile'] = df.groupby('bar_time')[factor_name].transform(
        lambda x: pd.qcut(x.rank(method='first'), N_QUANTILES, labels=False,
                          duplicates='drop') if len(x.dropna()) >= MIN_SAMPLES_PER_DAY else np.nan
    )

    df = df.dropna(subset=['quantile'])
    if len(df) == 0:
        return None

    # 每组每天等权平均收益
    daily_port = df.groupby(['bar_time', 'quantile'])['daily_ret'].mean().unstack()

    if daily_port.empty:
        return None

    # 多空组合
    if 0 in daily_port.columns and N_QUANTILES-1 in daily_port.columns:
        daily_port['LS'] = daily_port[N_QUANTILES-1] - daily_port[0]

    # 累计收益
    cum_ret = (1 + daily_port.fillna(0)).cumprod()

    # 年化指标
    def annualized(series, trading_days=242):
        if len(series) < 20:
            return {'ann_ret': np.nan, 'ann_vol': np.nan, 'sharpe': np.nan,
                    'max_dd': np.nan, 'win_rate': np.nan}
        total_days = len(series)
        years = total_days / trading_days
        ann_ret = (1 + series.mean()) ** trading_days - 1 if years > 0.1 else np.nan
        ann_vol = series.std() * np.sqrt(trading_days)
        sharpe = (ann_ret - 0.02) / ann_vol if ann_vol > 0 else 0
        # Max drawdown
        cum = (1 + series.fillna(0)).cumprod()
        peak = cum.expanding().max()
        dd = (cum / peak - 1)
        max_dd = dd.min()
        win_rate = (series > 0).mean()
        return {'ann_ret': ann_ret, 'ann_vol': ann_vol, 'sharpe': sharpe,
                'max_dd': max_dd, 'win_rate': win_rate}

    results = []
    cols = [c for c in daily_port.columns if isinstance(c, (int, np.integer))]
    if 'LS' in daily_port.columns:
        cols.append('LS')
    for q in cols:
        s = daily_port[q].dropna()
        metrics = annualized(s)
        results.append({
            'factor': factor_name,
            'period': period_label,
            'quantile': f'Q{int(q)+1}' if q != 'LS' else 'LS',
            'N_days': len(s),
            **metrics
        })
    return results

# ── 运行 ──────────────────────────────────────────

print(f"\n评估 {len(factor_cols)} 个因子...")

all_results = []
for i, fac in enumerate(factor_cols):
    # 全样本
    r_full = quantile_backtest(panel, fac, 'ALL')
    if r_full:
        all_results.extend(r_full)
    # IS only
    is_panel = panel[panel['period'] == 'IS']
    r_is = quantile_backtest(is_panel, fac, 'IS')
    if r_is:
        all_results.extend(r_is)
    # OOS only
    oos_panel = panel[panel['period'] == 'OOS']
    r_oos = quantile_backtest(oos_panel, fac, 'OOS')
    if r_oos:
        all_results.extend(r_oos)

    if (i + 1) % 5 == 0:
        print(f"  进度: {i+1}/{len(factor_cols)}")

qr_df = pd.DataFrame(all_results)
qr_df.to_csv(RESULT_PATH, index=False, encoding='utf-8-sig')

# ── 报告 ──────────────────────────────────────────

print(f"\n{'='*70}")
print(f"Quantile Portfolio — 多空组合表现 (OOS)")
print(f"{'='*70}")

ls_oos = qr_df[(qr_df['period'] == 'OOS') & (qr_df['quantile'] == 'LS')].copy()
ls_oos = ls_oos.sort_values('sharpe', ascending=False)

print(f"\n{'Factor':<20s} {'LS AnnRet':>10s} {'LS Vol':>8s} {'LS Sharpe':>10s} {'MaxDD':>8s} {'WinRate':>8s}")
print(f"{'-'*20} {'-'*10} {'-'*8} {'-'*10} {'-'*8} {'-'*8}")
for _, r in ls_oos.iterrows():
    star = " ★" if r['sharpe'] >= 0.5 else ""
    print(f"{r['factor']:<20s} {r['ann_ret']:10.2%} {r['ann_vol']:8.2%} {r['sharpe']:10.3f} {r['max_dd']:8.2%} {r['win_rate']:8.1%}{star}")

# ── 单调性检查 ─────────────────────────────────────

print(f"\n── Quantile 单调性 (OOS, Q1→Q5 收益是否递增) ──")
for fac in factor_cols[:8]:  # Top 8
    rows = qr_df[(qr_df['factor'] == fac) & (qr_df['period'] == 'OOS') & (qr_df['quantile'] != 'LS')]
    if len(rows) < N_QUANTILES:
        continue
    rets = rows.sort_values('quantile')['ann_ret'].values
    mono = "✓ 单调" if np.all(np.diff(rets) >= 0) or np.all(np.diff(rets) <= 0) else "✗ 非单调"
    print(f"  {fac:<20s}  ", end="")
    for r in rets:
        print(f"{r:8.2%}", end=" ")
    print(f"  {mono}")

print(f"\n[SAVED] {RESULT_PATH}")
print(f"\n★ LS Sharpe > 0.5 → 因子有实盘价值")
print(f"\n下一步: python scripts/factor_research/factor_report.py")
