# -*- coding: utf-8 -*-
import akshare as ak
import pandas as pd

df = ak.stock_zh_a_daily(symbol='sh601600', adjust='')  # 不复权
df = df.sort_values('date').reset_index(drop=True)

for n in [5, 13, 34, 89, 233]:
    df[f'ma{n}'] = df['close'].rolling(n).mean()

last = df.iloc[-1]
print("=== latest bar ===")
print(f"date={last['date']} close={last['close']:.2f}")
for n in [5, 13, 34, 89, 233]:
    v = last[f'ma{n}']
    above = "ABOVE" if last['close'] > v else "below"
    print(f"  ma{n:<4} = {v:7.2f}   close {above}")

print("\n=== ma34 / ma89 / ma233 recent 12 bars ===")
for i in range(-12, 0):
    r = df.iloc[i]
    print(f"{r['date']}  close={r['close']:6.2f}  ma34={r['ma34']:6.2f}  ma89={r['ma89']:6.2f}  ma233={r['ma233']:6.2f}")
