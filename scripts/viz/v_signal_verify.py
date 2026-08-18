"""V 信号准确性验证 — 价格+MA线+交叉点 vs 实际成交"""
import json, duckdb, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import matplotlib.dates as mdates
import numpy as np
from datetime import datetime

# Load trades
with open('configs/_v_debug.report.json') as f:
    r = json.load(f)
trades = r['strategyReports'][0]['trades']

# Load bar data for a sample period (first 2 months)
conn = duckdb.connect('data/continuous/v_continuous.duckdb', read_only=True)
bars = conn.execute("""
    SELECT bar_time, open, high, low, close
    FROM continuous_15min
    WHERE bar_time >= '2021-06-01' AND bar_time <= '2021-08-01'
    ORDER BY bar_time
""").fetchall()
conn.close()

times = [datetime.strptime(b[0], '%Y-%m-%d %H:%M:%S') for b in bars]
closes = np.array([b[4]/1e7 for b in bars])  # Prices in ×10⁷

# Compute MA(10) and MA(30)
def sma(data, period):
    result = np.full(len(data), np.nan)
    for i in range(period-1, len(data)):
        result[i] = np.mean(data[i-period+1:i+1])
    return result

ma10 = sma(closes, 10)
ma30 = sma(closes, 30)

# Find crossover points (golden cross / death cross)
crossovers = []
for i in range(1, len(ma10)):
    if np.isnan(ma10[i-1]) or np.isnan(ma30[i-1]):
        continue
    if ma10[i-1] <= ma30[i-1] and ma10[i] > ma30[i]:
        crossovers.append((times[i], closes[i], 'GOLDEN'))  # 金叉
    elif ma10[i-1] >= ma30[i-1] and ma10[i] < ma30[i]:
        crossovers.append((times[i], closes[i], 'DEATH'))   # 死叉

# Filter trades in this period
period_trades = []
for t in trades:
    et = datetime.fromisoformat(t['entryTime'].replace('Z','+00:00'))
    if et >= times[0] and et <= times[-1]:
        ep = float(t['entryPrice'])
        xp = float(t['exitPrice'])
        # Actual direction from PnL
        price_up = xp > ep
        pnl = t['pnL']
        actual_dir = 'LONG' if ((pnl > 0) == price_up) else 'SHORT'
        period_trades.append((et, ep, actual_dir, pnl, t['isWin'], t['quantity']))

# Plot
fig, ax = plt.subplots(figsize=(24, 10))

# Price
ax.plot(times, closes, color='#333', linewidth=0.8, alpha=0.7, label='Close')
ax.plot(times, ma10, color='#e67e22', linewidth=0.8, alpha=0.8, label='MA(10)')
ax.plot(times, ma30, color='#2980b9', linewidth=1.0, alpha=0.8, label='MA(30)')

# Crossovers
for ct, cp, ctype in crossovers:
    color = '#27ae60' if ctype == 'GOLDEN' else '#e74c3c'
    marker = '^' if ctype == 'GOLDEN' else 'v'
    ax.scatter(ct, cp, c=color, marker=marker, s=60, zorder=4,
              edgecolors='white', linewidth=0.5)

# Trades (entry points)
for et, ep, ad, pnl, is_win, lots in period_trades:
    color = '#27ae60' if is_win else '#e74c3c'
    marker = 'D' if ad == 'LONG' else 's'
    size = max(30, lots * 1.5)
    ax.scatter(et, ep, c=color, marker=marker, s=size, zorder=5,
              edgecolors='yellow' if is_win else 'black', linewidth=0.8)

ax.xaxis.set_major_formatter(mdates.DateFormatter('%m/%d'))
ax.xaxis.set_major_locator(mdates.DayLocator(interval=2))
ax.grid(alpha=0.2)
ax.set_title('V 15min — MA(10/30) Crossovers vs Actual Trades (Jun-Aug 2021)\n'
             '▲ Golden Cross (金叉)  ▼ Death Cross (死叉)  ◆ Long Entry  ■ Short Entry',
             fontsize=13, fontweight='bold')
ax.legend(loc='upper left')

fig.tight_layout()
fig.savefig('data/charts/v_signal_verify.png', dpi=150, bbox_inches='tight')
plt.close()
print('Saved: data/charts/v_signal_verify.png')

# Signal accuracy analysis
print(f'\n=== Signal Accuracy (Jun-Aug 2021) ===')
print(f'Total MA crossovers: {len(crossovers)}')
print(f'  Golden (金叉→Long):  {sum(1 for c in crossovers if c[2]=="GOLDEN")}')
print(f'  Death  (死叉→Short): {sum(1 for c in crossovers if c[2]=="DEATH")}')
print(f'Actual trades: {len(period_trades)}')

# Match trades to nearest crossover
matched = 0; unmatched = 0
for et, ep, ad, pnl, is_win, lots in period_trades:
    # Find nearest crossover within 2 bars
    nearest = None; min_dist = float('inf')
    for ct, cp, ctype in crossovers:
        dist = abs((et - ct).total_seconds())
        if dist < min_dist:
            min_dist = dist
            nearest = (ct, ctype)
    expected_dir = 'LONG' if nearest[1] == 'GOLDEN' else 'SHORT'
    if ad == expected_dir and min_dist < 3600:  # within 1 hour
        matched += 1
    else:
        unmatched += 1
        if unmatched <= 5:
            print(f'  UNMATCHED: trade at {et} dir={ad} nearest crossover={nearest[1]} at {nearest[0]} dist={min_dist/60:.0f}min')

print(f'  Matched trades: {matched}/{len(period_trades)}')
print(f'  Unmatched: {unmatched}')

# Print first 5 crossovers vs trades
print('\n=== First 10 Crossovers ===')
for i, (ct, cp, ctype) in enumerate(crossovers[:10]):
    nearby_trades = [(et, ad) for et, ep, ad, pnl, w, l in period_trades
                     if abs((et-ct).total_seconds()) < 7200]
    trade_str = ', '.join(f'{et.strftime("%m/%d %H:%M")} {ad}' for et, ad in nearby_trades)
    print(f'  {ct.strftime("%m/%d %H:%M")} {ctype:7s} @ {cp:.1f}  → trades: {trade_str or "(none)"}')
