# -*- coding: utf-8 -*-
import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import akshare as ak
import pandas as pd
from chanlun_core import Bar, Direction, analyze

# ---- 拉 AG2612 日线（新浪，不复权期货真实价） ----
df = ak.futures_zh_daily_sina(symbol='AG2612')
df = df.rename(columns=str.lower).sort_values('date').reset_index(drop=True)
df['date'] = pd.to_datetime(df['date'])

# ---- 五均线 ----
for n in [5, 13, 34, 89, 233]:
    df[f'ma{n}'] = df['close'].rolling(n).mean()

last = df.iloc[-1]
print("=== AG2612 五均线 (最新收盘) ===")
print(f"date={last['date'].date()}  close={last['close']:.0f}")
for n in [5, 13, 34, 89, 233]:
    v = last[f'ma{n}']
    print(f"  MA{n:<3} = {v:7.0f}   {'ABOVE' if last['close'] > v else 'below'}")

# 近几日均线走势
print("\n=== 近10日 close / ma34 / ma89 / ma233 ===")
for i in range(-10, 0):
    r = df.iloc[i]
    print(f"  {r['date'].date()}  close={r['close']:6.0f}  ma34={r['ma34']:6.0f}  ma89={r['ma89']:6.0f}  ma233={r['ma233']:6.0f}")

def to_bars(d):
    bars = []
    for _, r in d.iterrows():
        bars.append(Bar(dt=r['date'], open=float(r['open']), high=float(r['high']),
                        low=float(r['low']), close=float(r['close']), vol=int(r.get('volume', 0) or 0)))
    return bars

# ---- 日线缠论 ----
res_d = analyze(to_bars(df), min_bi_len=5, min_zs_overlap=1)
print("\n=== 日线笔 (共%d笔) ===" % len(res_d.bis))
for i, bi in enumerate(res_d.bis):
    arrow = '↑' if bi.type == Direction.Up else '↓'
    print(f"  Bi{i+1:2d} {arrow} {bi.dt_start.strftime('%m-%d')}({bi.start_fx.price:6.0f}) → {bi.dt_end.strftime('%m-%d')}({bi.end_fx.price:6.0f})  {bi.change_pct:+6.1f}%")

print("\n=== 日线中枢 ===")
for i, zs in enumerate(res_d.zhongshus):
    print(f"  ZS{i+1} [{zs.zd:.0f} ~ {zs.zg:.0f}]  {zs.dt_start.strftime('%m-%d') if zs.dt_start else '?'}~{zs.dt_end.strftime('%m-%d') if zs.dt_end else '?'}")

# ---- 周线缠论 ----
dfw = df.set_index('date').resample('W-FRI').agg(
    {'open': 'first', 'high': 'max', 'low': 'min', 'close': 'last', 'volume': 'sum'}).dropna().reset_index()
res_w = analyze(to_bars(dfw), min_bi_len=3, min_zs_overlap=1)
print("\n=== 周线笔 (共%d笔, %d根周K) ===" % (len(res_w.bis), len(dfw)))
for i, bi in enumerate(res_w.bis):
    arrow = '↑' if bi.type == Direction.Up else '↓'
    print(f"  Bi{i+1} {arrow} {bi.dt_start.strftime('%m-%d')}({bi.start_fx.price:6.0f}) → {bi.dt_end.strftime('%m-%d')}({bi.end_fx.price:6.0f})  {bi.change_pct:+6.1f}%")

print("\n=== 周线中枢 ===")
for i, zs in enumerate(res_w.zhongshus):
    print(f"  ZS{i+1} [{zs.zd:.0f} ~ {zs.zg:.0f}]  {zs.dt_start.strftime('%m-%d') if zs.dt_start else '?'}~{zs.dt_end.strftime('%m-%d') if zs.dt_end else '?'}")
