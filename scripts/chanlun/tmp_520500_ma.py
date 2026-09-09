# -*- coding: utf-8 -*-
import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import akshare as ak
import pandas as pd
from chanlun_core import Bar, Direction, analyze

# ---- 拉 520500 日线（东财 ETF，不复权） ----
df = ak.fund_etf_hist_em(symbol='520500', period='daily', start_date='20241201', end_date='20260909', adjust='')
# 列名中文化映射
colmap = {'日期': 'date', '开盘': 'open', '收盘': 'close', '最高': 'high', '最低': 'low', '成交量': 'volume'}
df = df.rename(columns=colmap)
df['date'] = pd.to_datetime(df['date'])
df = df.sort_values('date').reset_index(drop=True)

print(f"=== 520500 日线：{len(df)} 根，{df['date'].iloc[0].date()} ~ {df['date'].iloc[-1].date()} ===")

# ---- 五均线 ----
for n in [5, 13, 34, 89, 233]:
    df[f'ma{n}'] = df['close'].rolling(n).mean()

last = df.iloc[-1]
print("\n=== 五均线（最新收盘） ===")
print(f"date={last['date'].date()}  close={last['close']:.4f}")
for n in [5, 13, 34, 89, 233]:
    v = last[f'ma{n}']
    flag = 'ABOVE' if last['close'] > v else 'below'
    print(f"  MA{n:<3} = {v:.4f}   {flag}")

# 历史高低点
print("\n=== 历史关键位 ===")
imax = df['close'].idxmax()
imin = df['close'].idxmin()
print(f"  历史最高: {df['close'].max():.4f}  ({df.loc[imax,'date'].date()})")
print(f"  历史最低: {df['close'].min():.4f}  ({df.loc[imin,'date'].date()})")

def to_bars(d):
    return [Bar(dt=r['date'], open=float(r['open']), high=float(r['high']),
                low=float(r['low']), close=float(r['close']), vol=int(r.get('volume', 0) or 0))
            for _, r in d.iterrows()]

# ---- 日线缠论 ----
res_d = analyze(to_bars(df), min_bi_len=5, min_zs_overlap=1)
print(f"\n=== 日线笔 (共{len(res_d.bis)}笔) ===")
for i, bi in enumerate(res_d.bis):
    arrow = '↑' if bi.type == Direction.Up else '↓'
    print(f"  Bi{i+1:2d} {arrow} {bi.dt_start.strftime('%Y-%m-%d')}({bi.start_fx.price:6.4f}) → {bi.dt_end.strftime('%Y-%m-%d')}({bi.end_fx.price:6.4f})  {bi.change_pct:+6.1f}%")

print("\n=== 日线中枢 ===")
for i, zs in enumerate(res_d.zhongshus):
    print(f"  ZS{i+1} [{zs.zd:.4f} ~ {zs.zg:.4f}]  {zs.dt_start.strftime('%Y-%m-%d') if zs.dt_start else '?'}~{zs.dt_end.strftime('%Y-%m-%d') if zs.dt_end else '?'}")

# ---- 周线缠论 ----
dfw = df.set_index('date').resample('W-FRI').agg(
    {'open': 'first', 'high': 'max', 'low': 'min', 'close': 'last', 'volume': 'sum'}).dropna().reset_index()
res_w = analyze(to_bars(dfw), min_bi_len=3, min_zs_overlap=1)
print(f"\n=== 周线笔 (共{len(res_w.bis)}笔, {len(dfw)}根周K) ===")
for i, bi in enumerate(res_w.bis):
    arrow = '↑' if bi.type == Direction.Up else '↓'
    print(f"  Bi{i+1:2d} {arrow} {bi.dt_start.strftime('%Y-%m-%d')}({bi.start_fx.price:6.4f}) → {bi.dt_end.strftime('%Y-%m-%d')}({bi.end_fx.price:6.4f})  {bi.change_pct:+6.1f}%")

print("\n=== 周线中枢 ===")
for i, zs in enumerate(res_w.zhongshus):
    print(f"  ZS{i+1} [{zs.zd:.4f} ~ {zs.zg:.4f}]  {zs.dt_start.strftime('%Y-%m-%d') if zs.dt_start else '?'}~{zs.dt_end.strftime('%Y-%m-%d') if zs.dt_end else '?'}")
