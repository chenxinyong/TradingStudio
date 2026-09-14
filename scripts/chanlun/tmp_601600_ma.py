# -*- coding: utf-8 -*-
import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import akshare as ak
import pandas as pd

# ---- 拉 601600 中国铝业 日线（不复权） ----
df = ak.stock_zh_a_daily(symbol='sh601600', adjust='')
df = df.rename(columns=str.lower).sort_values('date').reset_index(drop=True)
df['date'] = pd.to_datetime(df['date'])

print(f"=== 601600 日线：{len(df)} 根，{df['date'].iloc[0].date()} ~ {df['date'].iloc[-1].date()} ===")

for n in [5, 13, 34, 89, 233]:
    df[f'ma{n}'] = df['close'].rolling(n).mean()

last = df.iloc[-1]
print(f"\n=== 五均线（最新收盘 {last['date'].date()}） ===")
print(f"close={last['close']:.2f}")
for n in [5, 13, 34, 89, 233]:
    v = last[f'ma{n}']
    flag = '上' if last['close'] > v else '下'
    print(f"  MA{n:<3}={v:8.2f}  {flag}")

# 近20日收盘，看最近走势
print("\n=== 最近 20 个交易日 收盘 ===")
for _, r in df.tail(20).iterrows():
    print(f"  {r['date'].date()}  O{r['open']:6.2f} H{r['high']:6.2f} L{r['low']:6.2f} C{r['close']:6.2f}")

# 2026-07 以来周度（反弹结构）
dfw = df[df['date'] >= '2026-06-01'].copy()
dfw['w'] = dfw['date'].dt.to_period('W')
g = dfw.groupby('w').agg(high=('high','max'), low=('low','min'), close=('close','last')).reset_index()
print("\n=== 2026-06 以来 周度 ===")
for _, r in g.iterrows():
    print(f"  {r['w']}  H{r['high']:6.2f} L{r['low']:6.2f} C{r['close']:6.2f}")
