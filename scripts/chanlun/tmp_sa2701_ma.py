# -*- coding: utf-8 -*-
import sys, io, os
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import akshare as ak
import pandas as pd
from chanlun_core import Bar, Direction, analyze

def load(sym):
    df = ak.futures_zh_daily_sina(symbol=sym)
    df = df.rename(columns=str.lower).sort_values('date').reset_index(drop=True)
    df['date'] = pd.to_datetime(df['date'])
    return df

def ma_table(df):
    for n in [5, 13, 34, 89, 233]:
        df[f'ma{n}'] = df['close'].rolling(n).mean()
    last = df.iloc[-1]
    print(f"date={last['date'].date()}  close={last['close']:.0f}")
    for n in [5, 13, 34, 89, 233]:
        v = last[f'ma{n}']
        flag = '上' if last['close'] > v else '下'
        print(f"  MA{n:<3}={v:8.0f}  {flag}")

def to_bars(d):
    return [Bar(dt=r['date'], open=float(r['open']), high=float(r['high']),
                low=float(r['low']), close=float(r['close']), vol=int(r.get('volume', 0) or 0))
            for _, r in d.iterrows()]

def run(res, label):
    print(f"--- {label} 笔 (共{len(res.bis)}笔) ---")
    for i, bi in enumerate(res.bis):
        arrow = '↑' if bi.type == Direction.Up else '↓'
        print(f"  Bi{i+1:2d} {arrow} {bi.dt_start.strftime('%Y-%m-%d')}({bi.start_fx.price:7.0f}) → {bi.dt_end.strftime('%Y-%m-%d')}({bi.end_fx.price:7.0f})  {bi.change_pct:+6.1f}%")
    if res.zhongshus:
        print(f"--- {label} 中枢 ---")
        for i, zs in enumerate(res.zhongshus):
            print(f"  ZS{i+1} [{zs.zd:.0f} ~ {zs.zg:.0f}]  {zs.dt_start.strftime('%Y-%m-%d') if zs.dt_start else '?'}~{zs.dt_end.strftime('%Y-%m-%d') if zs.dt_end else '?'}")

# ============ SA2701 单合约 ============
print("========== SA2701 单合约 ==========")
df1 = load('SA2701')
print(f"日线 {len(df1)} 根，{df1['date'].iloc[0].date()} ~ {df1['date'].iloc[-1].date()}")
print("--- 五均线 ---")
ma_table(df1)
print(f"历史最高 close {df1['close'].max():.0f} ({df1.loc[df1['close'].idxmax(),'date'].date()})")
print(f"历史最低 close {df1['close'].min():.0f} ({df1.loc[df1['close'].idxmin(),'date'].date()})")
run(analyze(to_bars(df1), min_bi_len=5, min_zs_overlap=1), "日线")
dfw1 = df1.set_index('date').resample('W-FRI').agg({'open':'first','high':'max','low':'min','close':'last','volume':'sum'}).dropna().reset_index()
run(analyze(to_bars(dfw1), min_bi_len=3, min_zs_overlap=1), "周线")

# ============ SA0 连续合约 ============
print("\n========== SA0 连续合约（长历史） ==========")
df0 = load('SA0')
print(f"日线 {len(df0)} 根，{df0['date'].iloc[0].date()} ~ {df0['date'].iloc[-1].date()}")
print("--- 五均线 ---")
ma_table(df0)
print(f"历史最高 close {df0['close'].max():.0f} ({df0.loc[df0['close'].idxmax(),'date'].date()})")
print(f"历史最低 close {df0['close'].min():.0f} ({df0.loc[df0['close'].idxmin(),'date'].date()})")
dfw0 = df0.set_index('date').resample('W-FRI').agg({'open':'first','high':'max','low':'min','close':'last','volume':'sum'}).dropna().reset_index()
run(analyze(to_bars(dfw0), min_bi_len=3, min_zs_overlap=1), "周线")
