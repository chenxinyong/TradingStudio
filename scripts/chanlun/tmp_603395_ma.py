# -*- coding: utf-8 -*-
import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import akshare as ak
import pandas as pd

# ---- 拉 603395 红四方 日线（不复权） ----
df = ak.stock_zh_a_daily(symbol='sh603395', adjust='')
df = df.rename(columns=str.lower).sort_values('date').reset_index(drop=True)
df['date'] = pd.to_datetime(df['date'])

print(f"=== 603395 红四方 日线：{len(df)} 根，{df['date'].iloc[0].date()} ~ {df['date'].iloc[-1].date()} ===")
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'tmp_603395.csv')
df.to_csv(out, index=False)

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

# 上市以来月度高低点（红四方 2024 上市，时间短，用全部数据）
dfm = df.copy()
dfm['ym'] = dfm['date'].dt.to_period('M')
g = dfm.groupby('ym').agg(high=('high','max'), low=('low','min'), close=('close','last'), first=('open','first')).reset_index()
print("\n=== 上市以来 月度 OHLC ===")
for _, r in g.iterrows():
    print(f"  {r['ym']}  O{r['first']:6.2f} H{r['high']:6.2f} L{r['low']:6.2f} C{r['close']:6.2f}")

# 上市以来周度（看最近走势细节）
dfw2 = df.copy()
dfw2['w'] = dfw2['date'].dt.to_period('W')
g2 = dfw2.groupby('w').agg(high=('high','max'), low=('low','min'), close=('close','last')).reset_index()
print("\n=== 上市以来 周度 ===")
for _, r in g2.iterrows():
    print(f"  {r['w']}  H{r['high']:6.2f} L{r['low']:6.2f} C{r['close']:6.2f}")
