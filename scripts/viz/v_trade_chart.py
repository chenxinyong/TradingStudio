"""V 交易信号可视化 — 每笔开平仓标记在价格图上"""
import json, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import matplotlib.dates as mdates
import numpy as np
from datetime import datetime
from collections import defaultdict

# Load data
with open('configs/_v_debug.report.json') as f:
    r = json.load(f)
trades = r['strategyReports'][0]['trades']

# Parse trades
entries = []  # (time, price, type, pnl, lots)
exits = []
for t in trades:
    et = datetime.fromisoformat(t['entryTime'].replace('Z','+00:00'))
    xt = datetime.fromisoformat(t['exitTime'].replace('Z','+00:00'))
    ep = float(t['entryPrice'])
    xp = float(t['exitPrice'])
    pnl = t['pnL']
    lots = t['quantity']
    # Infer direction from prices
    direction = 'Long' if xp > ep else 'Short'
    entries.append((et, ep, direction, pnl, lots))
    exits.append((xt, xp, direction, pnl, lots))

# Group by year for subplots
years = sorted(set(e[0].year for e in entries))
fig, axes = plt.subplots(len(years), 1, figsize=(20, 4*len(years)), sharex=False)
if len(years) == 1: axes = [axes]

for ax, yr in zip(axes, years):
    yr_entries = [(e[0], e[1], e[2], e[3], e[4]) for e in entries if e[0].year == yr]
    yr_exits = [(e[0], e[1], e[2], e[3], e[4]) for e in exits if e[0].year == yr]

    # Plot price line (approximate from trades)
    all_points = [(e[0], e[1]) for e in yr_entries] + [(e[0], e[1]) for e in yr_exits]
    all_points.sort()

    if all_points:
        times_p, prices = zip(*all_points)
        # Interpolate for smoother line
        ax.plot(times_p, prices, color='#555', linewidth=0.5, alpha=0.5, zorder=1)

    # Mark entries
    for et, ep, d, pnl, lots in yr_entries:
        color = '#27ae60' if d == 'Long' else '#e74c3c'
        marker = '^' if d == 'Long' else 'v'
        ax.scatter(et, ep, c=color, marker=marker, s=max(20, lots*2),
                  edgecolors='white', linewidth=0.5, zorder=3, alpha=0.8)

    # Mark exits
    for xt, xp, d, pnl, lots in yr_exits:
        win = pnl > 0
        color = '#27ae60' if win else '#e74c3c'
        marker = 'o'
        ax.scatter(xt, xp, c=color, marker=marker, s=max(15, lots*1.5),
                  edgecolors='white', linewidth=0.3, zorder=2, alpha=0.6)

    # Stats
    yr_trades = [t for t in trades if datetime.fromisoformat(t['exitTime'].replace('Z','+00:00')).year == yr]
    yr_wins = sum(1 for t in yr_trades if t['isWin'])
    yr_total = len(yr_trades)
    yr_pnl = sum(t['pnL'] for t in yr_trades)
    ax.set_title(f'{yr}  |  {yr_total} trades  Win={yr_wins/yr_total*100:.1f}%  PnL=¥{yr_pnl:,.0f}',
                 fontsize=12, fontweight='bold')
    ax.xaxis.set_major_formatter(mdates.DateFormatter('%m'))
    ax.xaxis.set_major_locator(mdates.MonthLocator())
    ax.grid(alpha=0.2)
    ax.set_ylabel('Price (¥/ton)')

fig.suptitle('V (PVC) Trade Signals — 双均线(10/30) 15min  |  Green=Long  Red=Short  ▲Entry  ●Exit',
             fontsize=14, fontweight='bold', y=1.01)
fig.tight_layout()
fig.savefig('data/charts/v_trade_signals.png', dpi=150, bbox_inches='tight')
plt.close()
print('Saved: data/charts/v_trade_signals.png')

# Also dump trade log
print(f'\nTotal: {len(trades)} trades')
print(f'Date range: {trades[0]["entryTime"][:10]} → {trades[-1]["exitTime"][:10]}')
