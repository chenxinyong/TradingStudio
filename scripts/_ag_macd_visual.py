"""
白银周线+日线 MACD面积背驰分析 + 缠论结构可视化
═══════════════════════════════════════════════════════
MACD面积法: 对每笔计算MACD柱面积，比较相邻同向笔
可视化: 周线缠论结构图 (K线+分型+笔+中枢+MACD)
"""

import sys, os, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "chanlun"))
from chanlun_core import *
import duckdb
import akshare as ak
import numpy as np
from datetime import datetime
from collections import defaultdict
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches
from matplotlib.dates import DateFormatter, WeekdayLocator
import warnings
warnings.filterwarnings('ignore')

# ── Config ──────────────────────────────────────────────────────
MACD_FAST, MACD_SLOW, MACD_SIGNAL = 12, 26, 9
DB = r"C:\Works\Datas\bars_history.duckdb"
OUTPUT = os.path.join(os.path.dirname(__file__), "..", "ag_weekly_chanlun.png")
con = duckdb.connect(DB, read_only=True)


# ── Load Data ───────────────────────────────────────────────────

def load_bars(table, symbol="ag000"):
    rows = con.execute(f"""
        SELECT bar_time, open, high, low, close, volume
        FROM {table} WHERE instrument_id = '{symbol}' ORDER BY bar_time
    """).fetchall()
    bars = []
    for r in rows:
        dt = r[0]
        if isinstance(dt, str): dt = datetime.fromisoformat(dt)
        bars.append(Bar(dt=dt, open=float(r[1])/1e7, high=float(r[2])/1e7,
                        low=float(r[3])/1e7, close=float(r[4])/1e7, vol=int(r[5] or 0)))
    return bars

# Fetch akshare data and merge
df = ak.futures_zh_daily_sina(symbol='AG0')
ak_bars = []
for _, row in df.iterrows():
    dt = datetime.strptime(str(row['date']), '%Y-%m-%d')
    ak_bars.append(Bar(dt=dt, open=float(row['open']), high=float(row['high']),
                       low=float(row['low']), close=float(row['close']), vol=int(row['volume'])))

# Merge daily
db_daily = load_bars("bars_day")
last_db = db_daily[-1].dt
new_daily = [b for b in ak_bars if b.dt > last_db]
daily_bars = db_daily + new_daily

# Build weekly from merged daily
def daily_to_weekly(bars):
    weeks = defaultdict(lambda: {"open": None, "high": float("-inf"),
                                  "low": float("inf"), "close": None, "vol": 0, "dt": None})
    for b in bars:
        wk = b.dt.isocalendar()
        key = (wk[0], wk[1])
        w = weeks[key]
        if w["open"] is None: w["open"] = b.open
        w["high"] = max(w["high"], b.high)
        w["low"] = min(w["low"], b.low)
        w["close"] = b.close; w["vol"] += b.vol; w["dt"] = b.dt
    result = []
    for key in sorted(weeks.keys()):
        w = weeks[key]
        result.append(Bar(dt=w["dt"], open=w["open"], high=w["high"],
                         low=w["low"], close=w["close"], vol=w["vol"]))
    return result

db_weekly = load_bars("bars_week")
ak_weekly = daily_to_weekly(ak_bars)
last_wk = db_weekly[-1].dt
new_weekly = [b for b in ak_weekly if b.dt > last_wk]
weekly_bars = db_weekly + new_weekly


# ── MACD Calculation ────────────────────────────────────────────

def calc_macd(closes):
    """Returns (dif, dea, histogram) arrays same length as closes."""
    closes = np.array(closes, dtype=float)
    ema_fast = np.zeros_like(closes)
    ema_slow = np.zeros_like(closes)
    dif = np.zeros_like(closes)
    dea = np.zeros_like(closes)
    hist = np.zeros_like(closes)

    alpha_f = 2.0 / (MACD_FAST + 1)
    alpha_s = 2.0 / (MACD_SLOW + 1)
    alpha_d = 2.0 / (MACD_SIGNAL + 1)

    for i in range(len(closes)):
        if i == 0:
            ema_fast[i] = closes[i]
            ema_slow[i] = closes[i]
        else:
            ema_fast[i] = alpha_f * closes[i] + (1 - alpha_f) * ema_fast[i-1]
            ema_slow[i] = alpha_s * closes[i] + (1 - alpha_s) * ema_slow[i-1]
        dif[i] = ema_fast[i] - ema_slow[i]
        if i == 0:
            dea[i] = dif[i]
        else:
            dea[i] = alpha_d * dif[i] + (1 - alpha_d) * dea[i-1]
        hist[i] = dif[i] - dea[i]

    return dif, dea, hist


def macd_area_for_bi(bi, bars, hist):
    """Calculate MACD histogram area for the span of a bi."""
    start, end = bi.start_idx, bi.end_idx
    seg = hist[start:end+1]
    # Area = sum of histogram values (simple sum — proportional to integral for equally spaced bars)
    return float(np.sum(seg))


def macd_divergence_analysis(result, bars):
    """Full MACD area divergence analysis for all same-direction bi pairs."""
    closes = np.array([b.close for b in bars])
    dif, dea, hist = calc_macd(closes)
    bis = result.bis

    findings = []
    for i in range(len(bis) - 1, 0, -1):
        bi = bis[i]
        # Find previous same-direction bi
        prev = None
        for j in range(i - 1, -1, -1):
            if bis[j].type == bi.type:
                prev = bis[j]
                break
        if prev is None:
            continue

        area_curr = macd_area_for_bi(bi, bars, hist)
        area_prev = macd_area_for_bi(prev, bars, hist)

        # For down bi (下跌笔): MACD area should be negative.
        # Divergence = price new low but area less negative (closer to 0)
        if bi.type == Direction.Down:
            price_new_low = bi.end_fx.price < prev.end_fx.price
            area_less_negative = area_curr > area_prev  # less negative = larger (closer to 0)
            if price_new_low and area_less_negative:
                findings.append({
                    "pair": f"笔{i+1} vs 笔{j+1}",
                    "type": "底背驰(买入)",
                    "price": f"{prev.end_fx.price:.0f}→{bi.end_fx.price:.0f}",
                    "area": f"{area_prev:.0f}→{area_curr:.0f}",
                    "current_bi": bi, "prev_bi": prev,
                })
        else:  # Up bi
            price_new_high = bi.end_fx.price > prev.end_fx.price
            area_less_positive = area_curr < area_prev  # less positive
            if price_new_high and area_less_positive:
                findings.append({
                    "pair": f"笔{i+1} vs 笔{j+1}",
                    "type": "顶背驰(卖出)",
                    "price": f"{prev.end_fx.price:.0f}→{bi.end_fx.price:.0f}",
                    "area": f"{area_prev:.0f}→{area_curr:.0f}",
                    "current_bi": bi, "prev_bi": prev,
                })

    return dif, dea, hist, findings


# ── Run Analysis ─────────────────────────────────────────────────

print("=" * 70)
print("  白银 ag000 MACD面积背驰分析 (数据至 2026-08-03)")
print("=" * 70)

# Weekly
print("\n[1] 周线分析...")
r_w = analyze(weekly_bars, min_bi_len=5)
dif_w, dea_w, hist_w, div_w = macd_divergence_analysis(r_w, weekly_bars)

print(f"  周线: {len(weekly_bars)} bars, {len(r_w.bis)} 笔, {len(r_w.zhongshus)} 中枢")
print(f"  MACD背驰信号: {len(div_w)} 个")
for d in div_w:
    print(f"    ★ {d['type']} {d['pair']}: 价格 {d['price']}, MACD面积 {d['area']}")

# Daily
print("\n[2] 日线分析...")
r_d = analyze(daily_bars, min_bi_len=5)
dif_d, dea_d, hist_d, div_d = macd_divergence_analysis(r_d, daily_bars)

print(f"  日线: {len(daily_bars)} bars, {len(r_d.bis)} 笔, {len(r_d.zhongshus)} 中枢")
print(f"  MACD背驰信号: {len(div_d)} 个")
for d in div_d[-5:]:  # last 5
    print(f"    ★ {d['type']} {d['pair']}: 价格 {d['price']}, MACD面积 {d['area']}")

# Weekly trend details
print(f"\n[3] 周线走势结构:")
print(f"  趋势分类: {r_w.trend}")
print(f"  中枢数: {len(r_w.zhongshus)}")
for zs in r_w.zhongshus:
    print(f"    ZS: [{zs.zd:.0f} ~ {zs.zg:.0f}] 中轨={zs.zz:.0f}")
last_bi = r_w.bis[-1]
last_zs = r_w.zhongshus[-1]
print(f"  最后一笔: {last_bi.type.name} {last_bi.dt_start.strftime('%Y-%m-%d')}→{last_bi.dt_end.strftime('%Y-%m-%d')}")
print(f"  末中枢: [{last_zs.zd:.0f} ~ {last_zs.zg:.0f}]")
if last_bi.end_fx.price < last_zs.zd:
    print(f"  ⚡ 价格 {last_bi.end_fx.price:.0f} < 中枢下沿 {last_zs.zd:.0f} — 跌破中枢!")
else:
    print(f"  价格 {last_bi.end_fx.price:.0f} 在中枢范围内")


# ── Chart: Weekly ChanLun Structure ─────────────────────────────

print(f"\n[4] 画周线缠论结构图...")

# Convert bars to pandas for plotting
dates = [b.dt for b in weekly_bars]
closes = np.array([b.close for b in weekly_bars])

# Create chart
plt.rcParams['font.family'] = 'Microsoft YaHei'
plt.rcParams['font.size'] = 9
fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(20, 12), gridspec_kw={'height_ratios': [3, 1]})

# ── Top panel: K-line + Fractals + Bi + Zhongshu ──
# Plot candlesticks (simplified: use close line + shade)
ax1.plot(dates, closes, color='#333333', linewidth=1.5, alpha=0.8, zorder=2)

# Plot fractals
for f in r_w.fractals:
    if f.type == FractalType.Top:
        ax1.scatter(f.dt, f.price, marker='v', color='#22AA22', s=60, zorder=5, edgecolors='white', linewidth=0.5)
    else:
        ax1.scatter(f.dt, f.price, marker='^', color='#DD2222', s=60, zorder=5, edgecolors='white', linewidth=0.5)

# Plot bi lines
for bi in r_w.bis:
    color = '#DD2222' if bi.type == Direction.Up else '#22AA22'
    ax1.plot([bi.dt_start, bi.dt_end], [bi.start_fx.price, bi.end_fx.price],
             color=color, linewidth=2.5, alpha=0.7, zorder=3)

# Plot zhongshu rectangles using fill_between
from matplotlib.dates import date2num
colors_zs = plt.cm.Blues(np.linspace(0.3, 0.7, max(len(r_w.zhongshus), 1)))
for i, zs in enumerate(r_w.zhongshus):
    ax1.fill_between([zs.dt_start, zs.dt_end], zs.zd, zs.zg,
                     facecolor=colors_zs[i], edgecolor='#2255AA', linewidth=0.8, alpha=0.15)
    ax1.annotate(f'ZS{i+1}', (zs.dt_start, zs.zg), fontsize=7, color='#2255AA',
                xytext=(0, 5), textcoords='offset points', alpha=0.7)

# Highlight last bi
if r_w.bis:
    lb = r_w.bis[-1]
    ax1.plot([lb.dt_start, lb.dt_end], [lb.start_fx.price, lb.end_fx.price],
             color='#FF6600', linewidth=3.5, alpha=0.9, zorder=4)

# Highlight last zhongshu
if r_w.zhongshus:
    lz = r_w.zhongshus[-1]
    ax1.axhline(y=lz.zd, color='#AA3333', linestyle='--', linewidth=1, alpha=0.5)
    ax1.axhline(y=lz.zg, color='#AA3333', linestyle='--', linewidth=1, alpha=0.5)
    ax1.axhline(y=lz.zz, color='#AA3333', linestyle=':', linewidth=0.8, alpha=0.3)

ax1.set_title('白银连续合约 (ag000) 周线缠论结构 — MACD面积背驰分析', fontsize=14, fontweight='bold')
ax1.set_ylabel('价格 (元/千克)', fontsize=10)
ax1.grid(True, alpha=0.2)
ax1.xaxis.set_major_formatter(DateFormatter('%Y-%m'))
ax1.xaxis.set_major_locator(WeekdayLocator(interval=26))

# Legend
legend_elements = [
    mpatches.Patch(color='#DD2222', alpha=0.7, label='上升笔'),
    mpatches.Patch(color='#22AA22', alpha=0.7, label='下降笔'),
    mpatches.Patch(color='#FF6600', alpha=0.9, label='最后一笔'),
    plt.Line2D([0], [0], marker='^', color='w', markerfacecolor='#DD2222', markersize=8, label='底分型'),
    plt.Line2D([0], [0], marker='v', color='w', markerfacecolor='#22AA22', markersize=8, label='顶分型'),
    mpatches.Patch(facecolor=colors_zs[0], edgecolor='#2255AA', alpha=0.15, label='中枢'),
]
ax1.legend(handles=legend_elements, loc='upper left', fontsize=8, ncol=3)

# ── Bottom panel: MACD ──
ax2.plot(dates, dif_w, color='#333333', linewidth=1.2, label='DIF')
ax2.plot(dates, dea_w, color='#DD8800', linewidth=1.2, label='DEA')

# MACD histogram: green if positive, red if negative
colors_bar = ['#DD2222' if h < 0 else '#22AA22' for h in hist_w]
ax2.bar(dates, hist_w, color=colors_bar, width=5, alpha=0.5, label='MACD柱')

# Shade last bi span on MACD
if r_w.bis:
    lb = r_w.bis[-1]
    ax2.axvspan(lb.dt_start, lb.dt_end, facecolor='#FF6600', alpha=0.08)
    # Find previous same-direction bi
    prev_same = None
    for j in range(len(r_w.bis) - 2, -1, -1):
        if r_w.bis[j].type == lb.type:
            prev_same = r_w.bis[j]
            break
    if prev_same:
        ax2.axvspan(prev_same.dt_start, prev_same.dt_end, facecolor='#888888', alpha=0.08)

ax2.axhline(y=0, color='#999999', linewidth=0.5)
ax2.set_ylabel('MACD', fontsize=10)
ax2.legend(loc='upper left', fontsize=8)
ax2.grid(True, alpha=0.2)
ax2.xaxis.set_major_formatter(DateFormatter('%Y-%m'))
ax2.xaxis.set_major_locator(WeekdayLocator(interval=26))

plt.tight_layout()
plt.savefig(OUTPUT, dpi=150, bbox_inches='tight', facecolor='white')
print(f"  图表已保存: {OUTPUT}")

con.close()
print("\n" + "=" * 70)
print("  分析完成")
print("=" * 70)
