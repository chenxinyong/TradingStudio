# -*- coding: utf-8 -*-
import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import akshare as ak
import pandas as pd
from chanlun_core import Bar, Direction, analyze

# ---- 拉 000690 宝新能源 日线（不复权） ----
df = ak.stock_zh_a_daily(symbol='sz000690', adjust='')
df = df.rename(columns=str.lower).sort_values('date').reset_index(drop=True)
df['date'] = pd.to_datetime(df['date'])

print(f"=== 000690 日线：{len(df)} 根，{df['date'].iloc[0].date()} ~ {df['date'].iloc[-1].date()} ===")
df.to_csv('tmp_000690.csv', index=False)

for n in [5, 13, 34, 89, 233]:
    df[f'ma{n}'] = df['close'].rolling(n).mean()

last = df.iloc[-1]
print(f"\n=== 五均线（最新收盘 {last['date'].date()}） ===")
print(f"close={last['close']:.2f}")
for n in [5, 13, 34, 89, 233]:
    v = last[f'ma{n}']
    flag = '上' if last['close'] > v else '下'
    print(f"  MA{n:<3}={v:8.2f}  {flag}")

print(f"\n历史最高 close {df['close'].max():.2f} ({df.loc[df['close'].idxmax(),'date'].date()})")
print(f"历史最低 close {df['close'].min():.2f} ({df.loc[df['close'].idxmin(),'date'].date()})")

def to_bars(d):
    return [Bar(dt=r['date'], open=float(r['open']), high=float(r['high']),
                low=float(r['low']), close=float(r['close']), vol=int(r.get('volume', 0) or 0))
            for _, r in d.iterrows()]

# 日线缠论
res_d = analyze(to_bars(df), min_bi_len=5, min_zs_overlap=1)
print(f"\n=== 日线笔 (共{len(res_d.bis)}笔) ===")
for i, bi in enumerate(res_d.bis):
    arrow = '↑' if bi.type == Direction.Up else '↓'
    print(f"  Bi{i+1:2d} {arrow} {bi.dt_start.strftime('%Y-%m-%d')}({bi.start_fx.price:6.2f}) → {bi.dt_end.strftime('%Y-%m-%d')}({bi.end_fx.price:6.2f})  {bi.change_pct:+6.1f}%")
if res_d.zhongshus:
    print("=== 日线中枢 ===")
    for i, zs in enumerate(res_d.zhongshus):
        print(f"  ZS{i+1} [{zs.zd:.2f} ~ {zs.zg:.2f}]  {zs.dt_start.strftime('%Y-%m-%d') if zs.dt_start else '?'}~{zs.dt_end.strftime('%Y-%m-%d') if zs.dt_end else '?'}")

# 周线缠论
dfw = df.set_index('date').resample('W-FRI').agg({'open':'first','high':'max','low':'min','close':'last','volume':'sum'}).dropna().reset_index()
res_w = analyze(to_bars(dfw), min_bi_len=3, min_zs_overlap=1)
print(f"\n=== 周线笔 (共{len(res_w.bis)}笔) ===")
for i, bi in enumerate(res_w.bis):
    arrow = '↑' if bi.type == Direction.Up else '↓'
    print(f"  Bi{i+1:2d} {arrow} {bi.dt_start.strftime('%Y-%m-%d')}({bi.start_fx.price:6.2f}) → {bi.dt_end.strftime('%Y-%m-%d')}({bi.end_fx.price:6.2f})  {bi.change_pct:+6.1f}%")
if res_w.zhongshus:
    print("=== 周线中枢 ===")
    for i, zs in enumerate(res_w.zhongshus):
        print(f"  ZS{i+1} [{zs.zd:.2f} ~ {zs.zg:.2f}]  {zs.dt_start.strftime('%Y-%m-%d') if zs.dt_start else '?'}~{zs.dt_end.strftime('%Y-%m-%d') if zs.dt_end else '?'}")
