"""
COMEX白银 (XAG) 周线缠论分析 — 对照上海白银 ag000
═══════════════════════════════════════════════════════
"""

import sys, os, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "chanlun"))
from chanlun_core import *
import akshare as ak
import numpy as np
import pandas as pd
from datetime import datetime
from collections import defaultdict
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches
from matplotlib.dates import DateFormatter
import warnings
warnings.filterwarnings('ignore')

OUTPUT = os.path.join(os.path.dirname(__file__), "..", "comex_weekly_chanlun.png")

# ── Fetch COMEX silver data ────────────────────────────────────

print("获取 COMEX 白银数据...")
df_daily = ak.futures_foreign_hist(symbol='XAG')
df_daily = df_daily.rename(columns={
    'date': 'Date', 'open': 'Open', 'high': 'High',
    'low': 'Low', 'close': 'Close', 'volume': 'Volume'
})
df_daily['Date'] = pd.to_datetime(df_daily['Date'])
df_daily = df_daily.dropna()

# Resample to weekly
df_daily = df_daily.set_index('Date')
df_weekly = df_daily.resample('W-FRI').agg({
    'Open': 'first', 'High': 'max', 'Low': 'min',
    'Close': 'last', 'Volume': 'sum'
}).dropna().reset_index()

print(f"  COMEX周线: {len(df_weekly)} 根, {df_weekly['Date'].iloc[0].strftime('%Y-%m-%d')} ~ {df_weekly['Date'].iloc[-1].strftime('%Y-%m-%d')}")

# Convert to chanlun Bars
bars = []
for _, row in df_weekly.iterrows():
    bars.append(Bar(
        dt=row['Date'].to_pydatetime(),
        open=float(row['Open']), high=float(row['High']),
        low=float(row['Low']), close=float(row['Close']),
        vol=int(row['Volume'])
    ))

# ── Run ChanLun ────────────────────────────────────────────────

result = analyze(bars, min_bi_len=5)

# ── MACD ───────────────────────────────────────────────────────

closes = np.array([b.close for b in bars])
def calc_macd(closes):
    ema_f, ema_s = np.zeros_like(closes), np.zeros_like(closes)
    dif, dea, hist = np.zeros_like(closes), np.zeros_like(closes), np.zeros_like(closes)
    af, as_, ad = 2.0/13, 2.0/27, 2.0/10
    for i in range(len(closes)):
        ema_f[i] = closes[i] if i==0 else af*closes[i]+(1-af)*ema_f[i-1]
        ema_s[i] = closes[i] if i==0 else as_*closes[i]+(1-as_)*ema_s[i-1]
        dif[i] = ema_f[i]-ema_s[i]
        dea[i] = dif[i] if i==0 else ad*dif[i]+(1-ad)*dea[i-1]
        hist[i] = dif[i]-dea[i]
    return dif, dea, hist

dif, dea, hist = calc_macd(closes)

# ── MACD Divergence ────────────────────────────────────────────

def find_divergences(bis, bars, hist):
    findings = []
    for i in range(len(bis)-1, 0, -1):
        bi = bis[i]
        prev = None; prev_j = -1
        for j in range(i-1, -1, -1):
            if bis[j].type == bi.type:
                prev = bis[j]; prev_j = j; break
        if prev is None: continue

        area_c = float(np.sum(hist[bi.start_idx:bi.end_idx+1]))
        area_p = float(np.sum(hist[prev.start_idx:prev.end_idx+1]))

        if bi.type == Direction.Down:
            if bi.end_fx.price < prev.end_fx.price and area_c > area_p:
                findings.append({"type": "底背驰(买入)", "bi": i+1, "prev_bi": prev_j+1,
                                 "price": f"{prev.end_fx.price:.1f}→{bi.end_fx.price:.1f}",
                                 "area": f"{area_p:.0f}→{area_c:.0f}",
                                 "dt": bi.dt_end})
        else:
            if bi.end_fx.price > prev.end_fx.price and area_c < area_p:
                findings.append({"type": "顶背驰(卖出)", "bi": i+1, "prev_bi": prev_j+1,
                                 "price": f"{prev.end_fx.price:.1f}→{bi.end_fx.price:.1f}",
                                 "area": f"{area_p:.0f}→{area_c:.0f}",
                                 "dt": bi.dt_end})
    # Return most recent of each type
    return findings

divs = find_divergences(result.bis, bars, hist)

# ── Print Results ──────────────────────────────────────────────

print(f"\n{'='*60}")
print(f"  COMEX白银 (XAG) 周线缠论分析")
print(f"{'='*60}")
print(f"  数据: {bars[0].dt.strftime('%Y-%m-%d')} ~ {bars[-1].dt.strftime('%Y-%m-%d')}")
print(f"  最新收盘: {bars[-1].close:.2f} 美元/盎司")
print(f"  K线: {len(bars)}  标准K线: {len(result.std_bars)}  分型: {len(result.fractals)}")
print(f"  笔: {len(result.bis)}  中枢: {len(result.zhongshus)}  趋势: {result.trend}")

# Last 8 bis
print(f"\n  ── 最近8笔 ──")
for bi in result.bis[-8:]:
    arrow = "↑" if bi.type == Direction.Up else "↓"
    print(f"  {arrow} {bi.dt_start.strftime('%Y-%m-%d')} → {bi.dt_end.strftime('%Y-%m-%d')}  "
          f"{bi.start_fx.price:.1f} → {bi.end_fx.price:.1f}  {bi.change_pct:+.1f}%  K线:{bi.bar_count}")

# Last zhongshu
if result.zhongshus:
    zs = result.zhongshus[-1]
    print(f"\n  ── 末中枢 ──")
    print(f"  [{zs.zd:.1f} ~ {zs.zg:.1f}] 中轨={zs.zz:.1f}  笔{zs.start_bi_idx+1}-{zs.end_bi_idx+1}")
    lb = result.bis[-1]
    if lb.end_fx.price < zs.zd:
        print(f"  ⚡ 价格 {lb.end_fx.price:.1f} < 中枢下沿 {zs.zd:.1f} — 跌破中枢!")
    elif lb.end_fx.price > zs.zg:
        print(f"  ⚡ 价格 {lb.end_fx.price:.1f} > 中枢上沿 {zs.zg:.1f} — 升破中枢!")

# Divergences (last 3)
print(f"\n  ── MACD面积背驰 (共{len(divs)}个, 末3个) ──")
for d in divs[:3]:
    print(f"  ★ {d['type']} 笔{d['bi']} vs 笔{d['prev_bi']}  "
          f"价格:{d['price']}  面积:{d['area']}  {d['dt'].strftime('%Y-%m-%d')}")

# ── Compare with Shanghai ──────────────────────────────────────

print(f"\n{'='*60}")
print(f"  COMEX vs 上海白银 (ag000) 对照")
print(f"{'='*60}")
print(f"  {'指标':20s} {'COMEX XAG':>15s} {'上海 ag000':>15s}")
print(f"  {'─'*20} {'─'*15} {'─'*15}")
print(f"  {'最新价':20s} {bars[-1].close:>15.2f} {'14218':>15s}")
print(f"  {'周线趋势':20s} {result.trend:>15s} {'UP':>15s}")
print(f"  {'周线笔数':20s} {len(result.bis):>15d} {25:>15d}")
print(f"  {'周线中枢数':20s} {len(result.zhongshus):>15d} {6:>15d}")

# Most recent bottom divergence
has_bottom = [d for d in divs if "底背驰" in d['type']]
print(f"  {'底背驰信号':20s} {'YES' if has_bottom else 'NO':>15s} {'YES (笔25 vs 23)':>15s}")

if has_bottom:
    bd = has_bottom[0]
    print(f"  {'  - 价格':20s} {bd['price']:>15s}")
    print(f"  {'  - MACD面积':20s} {bd['area']:>15s}")
    print(f"  {'  - 日期':20s} {bd['dt'].strftime('%Y-%m-%d'):>15s}")

# Price below last zhongshu?
if result.zhongshus:
    last_zs = result.zhongshus[-1]
    print(f"  {'末中枢':20s} {f'[{last_zs.zd:.1f}~{last_zs.zg:.1f}]':>15s} {'[15076~22051]':>15s}")
    lb = result.bis[-1]
    comex_below = "YES" if lb.end_fx.price < last_zs.zd else "NO"
    print(f"  {'价格破中枢下沿':20s} {comex_below:>15s} {'YES':>15s}")

# ── Chart ──────────────────────────────────────────────────────

print(f"\n  生成图表...")
plt.rcParams['font.family'] = 'Microsoft YaHei'
fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(20, 10), gridspec_kw={'height_ratios': [3, 1]})

dates = [b.dt for b in bars]

# Price panel
ax1.plot(dates, closes, color='#333', linewidth=1.2, alpha=0.7)

# Fractals
for f in result.fractals:
    marker = 'v' if f.type == FractalType.Top else '^'
    color = '#22AA22' if f.type == FractalType.Top else '#DD2222'
    ax1.scatter(f.dt, f.price, marker=marker, color=color, s=40, zorder=5, edgecolors='white', linewidth=0.3)

# Bi lines
for bi in result.bis:
    c = '#DD2222' if bi.type == Direction.Up else '#22AA22'
    ax1.plot([bi.dt_start, bi.dt_end], [bi.start_fx.price, bi.end_fx.price],
             color=c, linewidth=2, alpha=0.6, zorder=3)

# Zhongshu
colors_zs = plt.cm.Blues(np.linspace(0.3, 0.7, max(len(result.zhongshus), 1)))
for i, zs in enumerate(result.zhongshus):
    ax1.fill_between([zs.dt_start, zs.dt_end], zs.zd, zs.zg,
                     facecolor=colors_zs[i], edgecolor='#2255AA', linewidth=0.5, alpha=0.12)
    ax1.annotate(f'ZS{i+1}', (zs.dt_start, zs.zg), fontsize=6, color='#2255AA',
                xytext=(0, 3), textcoords='offset points', alpha=0.6)

# Highlight last bi
if result.bis:
    lb = result.bis[-1]
    ax1.plot([lb.dt_start, lb.dt_end], [lb.start_fx.price, lb.end_fx.price],
             color='#FF6600', linewidth=3, alpha=0.9, zorder=4)

# Last zhongshu lines
if result.zhongshus:
    lz = result.zhongshus[-1]
    ax1.axhline(y=lz.zd, color='#AA3333', linestyle='--', linewidth=0.8, alpha=0.4)
    ax1.axhline(y=lz.zg, color='#AA3333', linestyle='--', linewidth=0.8, alpha=0.4)

ax1.set_title('COMEX白银 (XAG) 周线缠论结构', fontsize=14, fontweight='bold')
ax1.set_ylabel('美元/盎司', fontsize=10)
ax1.grid(True, alpha=0.15)
ax1.xaxis.set_major_formatter(DateFormatter('%Y-%m'))

# MACD panel
ax2.plot(dates, dif, color='#333', linewidth=1, label='DIF')
ax2.plot(dates, dea, color='#DD8800', linewidth=1, label='DEA')
colors_bar = ['#DD2222' if h < 0 else '#22AA22' for h in hist]
ax2.bar(dates, hist, color=colors_bar, width=4, alpha=0.4)
# Shade last bi
if result.bis:
    lb = result.bis[-1]
    ax2.axvspan(lb.dt_start, lb.dt_end, facecolor='#FF6600', alpha=0.06)
ax2.axhline(y=0, color='#999', linewidth=0.5)
ax2.set_ylabel('MACD', fontsize=10)
ax2.grid(True, alpha=0.15)
ax2.xaxis.set_major_formatter(DateFormatter('%Y-%m'))

plt.tight_layout()
plt.savefig(OUTPUT, dpi=150, bbox_inches='tight', facecolor='white')
print(f"  图表: {OUTPUT}")
print(f"\n  完成。")
