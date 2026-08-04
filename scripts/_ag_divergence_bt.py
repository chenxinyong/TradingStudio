"""
周线 MACD 底背驰信号历史胜率统计
═══════════════════════════════════════
对白银周线历史上每次 MACD 底背驰信号，统计后续 N 周的胜率
"""

import sys, os, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "chanlun"))
from chanlun_core import *
import duckdb
import akshare as ak
import numpy as np
from datetime import datetime, timedelta
from collections import defaultdict

DB = r"C:\Works\Datas\bars_history.duckdb"
con = duckdb.connect(DB, read_only=True)

# ── Load & Merge ────────────────────────────────────────────────

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

def daily_to_weekly(bars):
    weeks = defaultdict(lambda: {"open": None, "high": float("-inf"),
                                  "low": float("inf"), "close": None, "vol": 0, "dt": None})
    for b in bars:
        wk = b.dt.isocalendar(); key = (wk[0], wk[1])
        w = weeks[key]
        if w["open"] is None: w["open"] = b.open; w["dt"] = b.dt
        w["high"] = max(w["high"], b.high); w["low"] = min(w["low"], b.low)
        w["close"] = b.close; w["vol"] += b.vol
    result = []
    for key in sorted(weeks.keys()):
        w = weeks[key]
        result.append(Bar(dt=w["dt"], open=w["open"], high=w["high"],
                         low=w["low"], close=w["close"], vol=w["vol"]))
    return result

# Merge data
df = ak.futures_zh_daily_sina(symbol='AG0')
ak_bars = [Bar(dt=datetime.strptime(str(r['date']), '%Y-%m-%d'),
               open=float(r['open']), high=float(r['high']),
               low=float(r['low']), close=float(r['close']),
               vol=int(r['volume'])) for _, r in df.iterrows()]

db_daily = load_bars("bars_day")
last_db = db_daily[-1].dt
daily_bars = db_daily + [b for b in ak_bars if b.dt > last_db]
weekly_bars = daily_to_weekly(daily_bars)

# ── MACD ────────────────────────────────────────────────────────

def calc_macd(closes):
    closes = np.array(closes, dtype=float)
    ema_fast, ema_slow = np.zeros_like(closes), np.zeros_like(closes)
    dif, dea, hist = np.zeros_like(closes), np.zeros_like(closes), np.zeros_like(closes)
    af, as_, ad = 2.0/13, 2.0/27, 2.0/10
    for i in range(len(closes)):
        ema_fast[i] = closes[i] if i == 0 else af * closes[i] + (1-af) * ema_fast[i-1]
        ema_slow[i] = closes[i] if i == 0 else as_ * closes[i] + (1-as_) * ema_slow[i-1]
        dif[i] = ema_fast[i] - ema_slow[i]
        dea[i] = dif[i] if i == 0 else ad * dif[i] + (1-ad) * dea[i-1]
        hist[i] = dif[i] - dea[i]
    return dif, dea, hist

closes_w = np.array([b.close for b in weekly_bars])
dif_w, dea_w, hist_w = calc_macd(closes_w)

# ── ChanLun ─────────────────────────────────────────────────────

result = analyze(weekly_bars, min_bi_len=5)
bis = result.bis

# ── Find all MACD bottom divergence signals ─────────────────────

print("=" * 70)
print("  周线 MACD底背驰信号 — 历史胜率回溯")
print("=" * 70)

signals = []
for i in range(2, len(bis)):
    bi = bis[i]
    if bi.type != Direction.Down:
        continue
    # Find previous down bi
    prev_dn = None
    prev_idx = -1
    for j in range(i - 1, -1, -1):
        if bis[j].type == Direction.Down:
            prev_dn = bis[j]
            prev_idx = j
            break
    if prev_dn is None:
        continue

    # MACD area comparison
    area_curr = float(np.sum(hist_w[bi.start_idx:bi.end_idx+1]))
    area_prev = float(np.sum(hist_w[prev_dn.start_idx:prev_dn.end_idx+1]))
    price_new_low = bi.end_fx.price < prev_dn.end_fx.price
    area_weakening = area_curr > area_prev  # less negative

    if price_new_low and area_weakening:
        # Found bottom divergence signal
        signal_date = bi.dt_end
        signal_price = bi.end_fx.price

        # Find subsequent price action
        # Map bi end dt to raw weekly bar index (bi indices point to std_bars, not raw)
        sig_dt = bi.dt_end
        sig_bar_idx = None
        for k, b in enumerate(weekly_bars):
            if b.dt >= sig_dt:
                sig_bar_idx = k
                break
        if sig_bar_idx is None:
            continue

        # Forward returns at 4, 8, 13, 26, 52 weeks
        fwd = {}
        for horizon in [4, 8, 13, 26, 52]:
            tgt_idx = sig_bar_idx + horizon
            if tgt_idx < len(closes_w):
                ret = (closes_w[tgt_idx] - signal_price) / signal_price * 100
                fwd[f"{horizon}周"] = ret
            else:
                fwd[f"{horizon}周"] = None  # Not enough data yet

        # Max drawdown after signal
        max_dd = 0.0
        peak = signal_price
        for k in range(sig_bar_idx, min(sig_bar_idx + 26, len(closes_w))):
            if closes_w[k] > peak:
                peak = closes_w[k]
            dd = (closes_w[k] - peak) / peak * 100
            if dd < max_dd:
                max_dd = dd

        signals.append({
            "date": signal_date,
            "price": signal_price,
            "bi_curr": f"笔{i+1}",
            "bi_prev": f"笔{prev_idx+1}",
            "area_curr": area_curr,
            "area_prev": area_prev,
            "area_pct": (area_curr - area_prev) / abs(area_prev) * 100 if area_prev != 0 else 0,
            "fwd": fwd,
            "max_dd_26w": max_dd,
        })

# ── Print ────────────────────────────────────────────────────────

print(f"\n  共发现 {len(signals)} 次周线MACD底背驰信号\n")

for s in signals:
    is_current = (s['date'] == signals[-1]['date'])
    marker = " ★ 当前信号" if is_current else ""
    print(f"  {s['date'].strftime('%Y-%m-%d')} | 价格:{s['price']:.0f} | "
          f"{s['bi_curr']} vs {s['bi_prev']} | "
          f"MACD面积:{s['area_prev']:.0f}→{s['area_curr']:.0f} ({s['area_pct']:+.0f}%){marker}")
    print(f"    未来收益: ", end="")
    for h, v in s['fwd'].items():
        if v is None:
            print(f"{h}=N/A  ", end="")
        else:
            emoji = "✓" if v > 0 else "✗"
            print(f"{h}={v:+.1f}%{emoji}  ", end="")
    print(f"| 最大回撤(26w): {s['max_dd_26w']:.1f}%")

# ── Summary stats ────────────────────────────────────────────────

print(f"\n{'='*70}")
print(f"  胜率汇总")
print(f"{'='*70}")

for horizon in [4, 8, 13, 26, 52]:
    vals = [s['fwd'].get(f"{horizon}周") for s in signals if s['fwd'].get(f"{horizon}周") is not None]
    if not vals:
        print(f"  {horizon:3d}周: 数据不足")
        continue
    wins = sum(1 for v in vals if v > 0)
    total = len(vals)
    avg = np.mean(vals)
    median = np.median(vals)
    print(f"  {horizon:3d}周: 胜率 {wins}/{total} ({wins/total*100:.0f}%)  均值 {avg:+.1f}%  中位数 {median:+.1f}%")

# Maximum drawdown stats
dd_vals = [s['max_dd_26w'] for s in signals]
print(f"\n  26周最大回撤: 均值 {np.mean(dd_vals):.1f}%  最差 {min(dd_vals):.1f}%  中位数 {np.median(dd_vals):.1f}%")

con.close()
