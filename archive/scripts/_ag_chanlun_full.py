"""
补齐最近5周数据，重跑缠论三维分析
数据源: DuckDB (至 6/30) + akshare (7/1 ~ 8/3)
"""

import sys, os, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "chanlun"))
from chanlun_core import *
import duckdb
import akshare as ak
from datetime import datetime, timedelta

DB = r"C:\Works\Datas\bars_history.duckdb"
con = duckdb.connect(DB, read_only=True)


# ── 1. Load existing data from DuckDB ────────────────────────────

def load_daily_bars(symbol="ag000"):
    rows = con.execute(f"""
        SELECT bar_time, open, high, low, close, volume
        FROM bars_day WHERE instrument_id = '{symbol}'
        ORDER BY bar_time
    """).fetchall()
    bars = []
    for r in rows:
        dt = r[0]
        if isinstance(dt, str): dt = datetime.fromisoformat(dt)
        bars.append(Bar(dt=dt, open=float(r[1])/1e7, high=float(r[2])/1e7,
                        low=float(r[3])/1e7, close=float(r[4])/1e7, vol=int(r[5] or 0)))
    return bars

def load_weekly_bars(symbol="ag000"):
    rows = con.execute(f"""
        SELECT bar_time, open, high, low, close, volume
        FROM bars_week WHERE instrument_id = '{symbol}'
        ORDER BY bar_time
    """).fetchall()
    bars = []
    for r in rows:
        dt = r[0]
        if isinstance(dt, str): dt = datetime.fromisoformat(dt)
        bars.append(Bar(dt=dt, open=float(r[1])/1e7, high=float(r[2])/1e7,
                        low=float(r[3])/1e7, close=float(r[4])/1e7, vol=int(r[5] or 0)))
    return bars


# ── 2. Fetch missing data from akshare ────────────────────────────

def fetch_recent_daily():
    """Fetch AG0 daily data from akshare, return bars from 2026-07-01 onwards."""
    df = ak.futures_zh_daily_sina(symbol='AG0')
    bars = []
    for _, row in df.iterrows():
        dt_str = str(row['date'])
        dt = datetime.strptime(dt_str, '%Y-%m-%d')
        bars.append(Bar(
            dt=dt,
            open=float(row['open']), high=float(row['high']),
            low=float(row['low']), close=float(row['close']),
            vol=int(row['volume'])
        ))
    return bars


def daily_to_weekly(daily_bars):
    """Aggregate daily bars to weekly bars (Mon-Fri)."""
    from collections import defaultdict
    weeks = defaultdict(lambda: {"open": None, "high": float("-inf"),
                                  "low": float("inf"), "close": None, "vol": 0, "dt": None})
    for b in daily_bars:
        # ISO week
        wk = b.dt.isocalendar()
        key = (wk[0], wk[1])  # (year, week_number)
        w = weeks[key]
        if w["open"] is None: w["open"] = b.open
        w["high"] = max(w["high"], b.high)
        w["low"] = min(w["low"], b.low)
        w["close"] = b.close
        w["vol"] += b.vol
        w["dt"] = b.dt  # last day of week

    result = []
    for key in sorted(weeks.keys()):
        w = weeks[key]
        result.append(Bar(dt=w["dt"], open=w["open"], high=w["high"],
                         low=w["low"], close=w["close"], vol=w["vol"]))
    return result


# ── 3. Analysis helpers ──────────────────────────────────────────

def analyze_divergence(bis, bars):
    if len(bis) < 3:
        return {"signal": "N/A", "detail": "不够笔数判断背驰"}
    last_bi = bis[-1]
    prev_same_dir = None
    for bi in reversed(bis[:-1]):
        if bi.type == last_bi.type:
            prev_same_dir = bi
            break
    if prev_same_dir is None:
        return {"signal": "N/A", "detail": "找不到前一同向笔"}

    last_range = abs(last_bi.end_fx.price - last_bi.start_fx.price)
    prev_range = abs(prev_same_dir.end_fx.price - prev_same_dir.start_fx.price)
    last_power = last_bi.power
    prev_power = prev_same_dir.power

    if last_bi.type == Direction.Up:
        price_extreme = last_bi.end_fx.price > prev_same_dir.end_fx.price
    else:
        price_extreme = last_bi.end_fx.price < prev_same_dir.end_fx.price

    signals = []
    if price_extreme: signals.append("价格超越前一笔")
    if last_power < prev_power: signals.append(f"力度衰减({last_power:.1f} vs {prev_power:.1f})")
    if last_range < prev_range: signals.append(f"空间缩小({last_range:.0f} vs {prev_range:.0f})")

    if price_extreme and (last_power < prev_power or last_range < prev_range):
        sig = "背驰(买入)" if last_bi.type == Direction.Down else "背驰(卖出)"
    else:
        sig = "无背驰"
    return {"signal": sig, "detail": ", ".join(signals) if signals else "力度未衰减"}


def print_compact(name, result):
    """Compact single-screen summary for one timeframe."""
    print(f"\n{'='*70}")
    print(f"  {name}  |  趋势:{result.trend:15s}  笔:{len(result.bis):3d}  中枢:{len(result.zhongshus):2d}")
    print(f"{'='*70}")

    # Last 5 bis
    if result.bis:
        print(f"  末5笔:")
        for bi in result.bis[-5:]:
            arrow = "↑" if bi.type == Direction.Up else "↓"
            print(f"  {arrow} {bi.dt_start.strftime('%Y-%m-%d')} → {bi.dt_end.strftime('%Y-%m-%d')}  "
                  f"{bi.start_fx.price:.1f} → {bi.end_fx.price:.1f}  "
                  f"幅度:{bi.power:.1f}‱  变化:{bi.change_pct:+.2f}%  K线:{bi.bar_count}")

    # Last zhongshu
    if result.zhongshus:
        zs = result.zhongshus[-1]
        print(f"  末中枢: [{zs.zd:.1f} ~ {zs.zg:.1f}]  中轨={zs.zz:.1f}  "
              f"笔{zs.start_bi_idx+1}-{zs.end_bi_idx+1}  {zs.dt_start} ~ {zs.dt_end}")

    # Divergence
    if len(result.bis) >= 3:
        div = analyze_divergence(result.bis, result.std_bars)
        print(f"  背驰: {div['signal']}  |  {div['detail']}")

    # Price range
    if result.raw_bars:
        print(f"  数据: {result.raw_bars[0].dt.strftime('%Y-%m-%d')} ~ {result.raw_bars[-1].dt.strftime('%Y-%m-%d')}  "
              f"最后收盘: {result.raw_bars[-1].close:.1f}")


# ── 4. Main ──────────────────────────────────────────────────────

print("=" * 70)
print("  白银 ag000 缠论三维分析 (数据截止 2026-08-03, akshare补全)")
print("=" * 70)

# ── Daily: merge DuckDB + akshare ──
print("\n[1/3] 加载日线数据...")
db_daily = load_daily_bars()
ak_daily_all = fetch_recent_daily()
last_db_date = db_daily[-1].dt
print(f"  DuckDB: {len(db_daily)} bars, 截止 {last_db_date.strftime('%Y-%m-%d')}")
print(f"  akshare: {len(ak_daily_all)} bars, 截止 {ak_daily_all[-1].dt.strftime('%Y-%m-%d')}")

# Merge: only append akshare bars after last duckdb date
new_daily = [b for b in ak_daily_all if b.dt > last_db_date]
daily_bars = db_daily + new_daily
print(f"  合并: {len(daily_bars)} bars, 新增 {len(new_daily)} bars ({new_daily[0].dt.strftime('%Y-%m-%d')} ~ {new_daily[-1].dt.strftime('%Y-%m-%d')})")

# ── Weekly: merge DuckDB + aggregate from daily ──
print("\n[2/3] 构建周线数据...")
db_weekly = load_weekly_bars()
last_wk_date = db_weekly[-1].dt
ak_weekly = daily_to_weekly(ak_daily_all)
new_weekly = [b for b in ak_weekly if b.dt > last_wk_date]
weekly_bars = db_weekly + new_weekly
print(f"  周线: {len(weekly_bars)} bars, 新增 {len(new_weekly)} bars")

# ── 1H: use existing DuckDB only (no intraday from akshare) ──
print("\n[3/3] 1H from DuckDB (akshare无日内数据)")

# ── Run analysis ──
print("\n\n" + "=" * 70)
print("  分析结果")
print("=" * 70)

r_w = analyze(weekly_bars, min_bi_len=5)
print_compact("周线 (MinBiLen=5)", r_w)

r_d = analyze(daily_bars, min_bi_len=5)
print_compact("日线 (MinBiLen=5)", r_d)

# 1H from DuckDB only
from collections import defaultdict
rows = con.execute("""
    SELECT bar_time, open, high, low, close, volume
    FROM bars_1min WHERE instrument_id = 'ag000'
    ORDER BY bar_time
""").fetchall()
hour_data = defaultdict(lambda: {"dt": None, "open": None, "high": float("-inf"),
                                  "low": float("inf"), "close": None, "vol": 0})
for r in rows:
    dt = r[0]
    if isinstance(dt, str): dt = datetime.fromisoformat(dt)
    hk = dt.replace(minute=0, second=0, microsecond=0)
    b = hour_data[hk]
    o, h, l, c, v = float(r[1])/1e7, float(r[2])/1e7, float(r[3])/1e7, float(r[4])/1e7, int(r[5] or 0)
    if b["open"] is None: b["dt"] = hk; b["open"] = o
    b["high"] = max(b["high"], h); b["low"] = min(b["low"], l)
    b["close"] = c; b["vol"] += v

hourly_bars = []
for hk in sorted(hour_data.keys()):
    b = hour_data[hk]
    hourly_bars.append(Bar(dt=b["dt"], open=b["open"], high=b["high"],
                          low=b["low"], close=b["close"], vol=b["vol"]))

r_h = analyze(hourly_bars, min_bi_len=7)
print_compact("1小时 (MinBiLen=7)", r_h)

# ── Summary table ──
print(f"\n\n{'='*70}")
print(f"  三层共振判定")
print(f"{'='*70}")

results = {"周线": r_w, "日线": r_d, "1H": r_h}
for name, r in results.items():
    div = analyze_divergence(r.bis, r.std_bars) if len(r.bis) >= 3 else {"signal": "N/A"}
    print(f"  {name:6s}  趋势:{r.trend:15s}  笔:{len(r.bis):3d}  中枢:{len(r.zhongshus):2d}  背驰:{div['signal']}")

# Last known price
print(f"\n  最后日线: {daily_bars[-1].dt.strftime('%Y-%m-%d')}  Close={daily_bars[-1].close:.1f}")
print(f"  最后周线: {weekly_bars[-1].dt.strftime('%Y-%m-%d')}  Close={weekly_bars[-1].close:.1f}")
print(f"  最后1HK线: {hourly_bars[-1].dt.strftime('%Y-%m-%d %H:%M')}  Close={hourly_bars[-1].close:.1f}")

con.close()
