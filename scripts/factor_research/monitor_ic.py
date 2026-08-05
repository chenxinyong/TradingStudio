"""
滚动IC监控 — 每日计算截面Rank IC，检测因子衰减。

用法: python scripts/factor_research/monitor_ic.py [--days 60] [--alert-threshold 0.3]

功能:
  1. 计算每个因子最近N天的滚动Rank IC
  2. 对比滚动IC与历史IC分布 (IS 2020-2023)
  3. 检测衰减: 滚动IC < 历史IC中位数 × 0.5 → 发出告警
  4. 输出Markdown报告

输出: scripts/factor_research/output/ic_monitor_report.md
"""

import sys, io, os, argparse
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

import pandas as pd, numpy as np
from scipy import stats
from datetime import datetime, timedelta

# ── 配置 ──
FACTOR_PANEL = "scripts/factor_research/output/factors_panel_v1.parquet"
OUTPUT = "scripts/factor_research/output/ic_monitor_report.md"
IS_END = "2023-12-31"

parser = argparse.ArgumentParser()
parser.add_argument('--days', type=int, default=60, help='滚动窗口天数')
parser.add_argument('--alert-threshold', type=float, default=0.3, help='衰减告警阈值(IC/历史中位数)')
args = parser.parse_args()

ROLLING_DAYS = args.days
ALERT_RATIO = args.alert_threshold

print(f"IC Monitor: rolling {ROLLING_DAYS}d window, alert ratio={ALERT_RATIO}")

# ── 加载 ──
panel = pd.read_parquet(FACTOR_PANEL)
panel['trading_day'] = pd.to_datetime(panel['trading_day'])
panel = panel.sort_values(['trading_day', 'instrument_id'])

# 因子列
exclude = ['instrument_id', 'trading_day', 'bar_time', 'period', 'FwdRet_1d']
factor_cols = [c for c in panel.columns if c not in exclude]
print(f"Factors: {len(factor_cols)}")

# ── 1. 构建 Panel (含前向收益) ──
panel['fwd_ret'] = panel.groupby('instrument_id')['FwdRet_1d'].shift(0)

# ── 2. 每日截面 Rank IC ──
def daily_rank_ic(grp, factor_col):
    """单日截面 Rank IC"""
    valid = grp.dropna(subset=[factor_col, 'fwd_ret'])
    if len(valid) < 5: return np.nan
    r, _ = stats.spearmanr(valid[factor_col], valid['fwd_ret'])
    return r

ic_series = {}
for fac in factor_cols:
    ic = panel.groupby('trading_day').apply(lambda g: daily_rank_ic(g, fac), include_groups=False)
    ic_series[fac] = ic.dropna()

# ── 3. 滚动IC vs 历史基准 ──
print(f"\n{'='*70}")
print(f"Rolling IC Monitor ({ROLLING_DAYS}d window)")
print(f"{'='*70}")

report_lines = []
report_lines.append(f"# 滚动IC监控报告")
report_lines.append(f"\n> 生成时间: {datetime.now().strftime('%Y-%m-%d %H:%M')}")
report_lines.append(f"> 滚动窗口: {ROLLING_DAYS}天 | 告警阈值: 衰减至历史中位数的{ALERT_RATIO*100:.0f}%")
report_lines.append(f"")

alerts = []

for fac in factor_cols:
    ic = ic_series[fac]
    if len(ic) < ROLLING_DAYS: continue

    # 历史基准 (IS)
    is_ic = ic[ic.index <= IS_END]
    if len(is_ic) < 100: continue
    hist_mean = is_ic.mean()
    hist_median = np.median(np.abs(is_ic))  # 用绝对值中位数作为基准
    hist_std = is_ic.std()

    # 滚动窗口
    rolling = ic.rolling(ROLLING_DAYS, min_periods=ROLLING_DAYS//2).mean()
    current = rolling.iloc[-1] if len(rolling) > 0 else np.nan
    prev = rolling.iloc[-2] if len(rolling) > 1 else np.nan

    # 当前 vs 历史
    decay_ratio = abs(current) / hist_median if hist_median > 0 else 1.0

    # 趋势: 最近10个滚动IC点的线性回归
    recent = rolling.dropna().iloc[-10:]
    trend = "→"
    if len(recent) >= 5:
        x = np.arange(len(recent))
        slope = np.polyfit(x, recent.values, 1)[0]
        if slope > 0.001: trend = "↑ 改善"
        elif slope < -0.001: trend = "↓ 衰减"

    # 告警判断
    status = "✅"
    if np.isnan(current):
        status = "⚪ 无数据"
    elif decay_ratio < ALERT_RATIO:
        status = "🔴 严重衰减"
        alerts.append(f"{fac}: IC从{hist_median:.4f}降至{abs(current):.4f} (衰减{decay_ratio:.0%})")
    elif decay_ratio < ALERT_RATIO * 2:
        status = "🟡 轻微衰减"

    report_lines.append(f"### {fac}")
    report_lines.append(f"| 指标 | 值 |")
    report_lines.append(f"|------|-----|")
    report_lines.append(f"| 历史IC均值(IS) | {hist_mean:+.4f} |")
    report_lines.append(f"| 历史|IC|中位数 | {hist_median:.4f} |")
    report_lines.append(f"| 当前滚动IC({ROLLING_DAYS}d) | {current:+.4f} |")
    report_lines.append(f"| 前一滚动IC | {prev:+.4f} |")
    report_lines.append(f"| 衰减比(当前/历史) | {decay_ratio:.1%} |")
    report_lines.append(f"| 趋势 | {trend} |")
    report_lines.append(f"| 状态 | {status} |")
    report_lines.append(f"")

    print(f"  {fac:20s}: hist_median={hist_median:.4f} current={current:+.4f} decay={decay_ratio:.0%} {status}")

# ── 4. 告警汇总 ──
report_lines.append(f"## 告警汇总")
if alerts:
    report_lines.append(f"\n**{len(alerts)} 个因子触发告警:**\n")
    for a in alerts: report_lines.append(f"- {a}")
else:
    report_lines.append(f"\n✅ 所有因子正常，无衰减告警。\n")

report_lines.append(f"\n---")
report_lines.append(f"*报告由 monitor_ic.py 自动生成*")

# Save
with open(OUTPUT, 'w', encoding='utf-8') as f:
    f.write('\n'.join(report_lines))

print(f"\nReport: {OUTPUT}")
print(f"Alerts: {len(alerts)}")
