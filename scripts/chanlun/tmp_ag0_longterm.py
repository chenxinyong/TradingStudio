# -*- coding: utf-8 -*-
import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import akshare as ak
import pandas as pd
from chanlun_core import Bar, Direction, analyze

# 连续合约 AG0（主力连续，长历史）
df = ak.futures_zh_daily_sina(symbol='AG0')
df = df.rename(columns=str.lower).sort_values('date').reset_index(drop=True)
df['date'] = pd.to_datetime(df['date'])

for n in [5, 13, 34, 89, 233]:
    df[f'ma{n}'] = df['close'].rolling(n).mean()

last = df.iloc[-1]
print("=== AG0 连续合约 五均线 (最新收盘) ===")
print(f"date={last['date'].date()}  close={last['close']:.0f}")
for n in [5, 13, 34, 89, 233]:
    v = last[f'ma{n}']
    print(f"  MA{n:<3} = {v:7.0f}   {'ABOVE' if last['close'] > v else 'below'}")

def to_bars(d):
    return [Bar(dt=r['date'], open=float(r['open']), high=float(r['high']),
                low=float(r['low']), close=float(r['close']), vol=int(r.get('volume', 0) or 0))
            for _, r in d.iterrows()]

# 周线
dfw = df.set_index('date').resample('W-FRI').agg(
    {'open': 'first', 'high': 'max', 'low': 'min', 'close': 'last', 'volume': 'sum'}).dropna().reset_index()
res_w = analyze(to_bars(dfw), min_bi_len=3, min_zs_overlap=1)
print(f"\n=== AG0 周线笔 (共{len(res_w.bis)}笔, {len(dfw)}根周K) ===")
for i, bi in enumerate(res_w.bis):
    arrow = '↑' if bi.type == Direction.Up else '↓'
    print(f"  Bi{i+1:2d} {arrow} {bi.dt_start.strftime('%Y-%m-%d')}({bi.start_fx.price:7.0f}) → {bi.dt_end.strftime('%Y-%m-%d')}({bi.end_fx.price:7.0f})  {bi.change_pct:+7.1f}%")

print("\n=== AG0 周线中枢 ===")
for i, zs in enumerate(res_w.zhongshus):
    print(f"  ZS{i+1} [{zs.zd:.0f} ~ {zs.zg:.0f}]  {zs.dt_start.strftime('%Y-%m-%d') if zs.dt_start else '?'}~{zs.dt_end.strftime('%Y-%m-%d') if zs.dt_end else '?'}")

# 历史关键位
print("\n=== AG0 历史关键位 ===")
print(f"  2024-2026 最高: {df['close'].max():.0f}  ({df.loc[df['close'].idxmax(), 'date'].date()})")
print(f"  2024-2026 最低: {df['close'].min():.0f}  ({df.loc[df['close'].idxmin(), 'date'].date()})")
