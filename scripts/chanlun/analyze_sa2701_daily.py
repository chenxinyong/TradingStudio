# -*- coding: utf-8 -*-
"""SA2701 日线缠论分析 — 判断第一类买点（临时脚本）"""
import sys, os
sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import pandas as pd
import akshare as ak
from chanlun_core import Bar, Direction, analyze as chanlun_analyze
from chanlun_batch import calc_macd, analyze_divergence

symbol = "SA2701"
df = ak.futures_zh_daily_sina(symbol=symbol)
df = df.rename(columns={"date": "Date", "open": "Open", "high": "High",
                        "low": "Low", "close": "Close", "volume": "Volume"})
df["Date"] = pd.to_datetime(df["Date"])
df = df[["Date", "Open", "High", "Low", "Close", "Volume"]].dropna().reset_index(drop=True)

print(f"数据范围: {df['Date'].iloc[0]:%Y-%m-%d} ~ {df['Date'].iloc[-1]:%Y-%m-%d}  共 {len(df)} 根日K")

# 最近 13 个交易日
recent = df.tail(13).copy()
recent["chg%"] = (recent["Close"].pct_change() * 100).round(2)
print("\n=== 最近 13 个交易日 ===")
for r in recent.itertuples():
    chg = getattr(r, "_2", None)  # chg% 列名
print(recent.to_string(index=False))

# 缠论分析（日线，全量）
bars = [Bar(dt=row.Date, open=float(row.Open), high=float(row.High),
            low=float(row.Low), close=float(row.Close), vol=int(row.Volume))
        for row in df.itertuples()]
res = chanlun_analyze(bars, min_bi_len=5, min_zs_overlap=5.0)

print(f"\n=== 缠论统计: 标准K {len(res.std_bars)} | 分型 {len(res.fractals)} | 笔 {len(res.bis)} | 中枢 {len(res.zhongshus)} | 走势 {res.trend}")

print("\n=== 最近笔（日线）===")
for b in res.bis[-12:]:
    print(f"  {b.type.value:4s} {b.dt_start:%Y-%m-%d}→{b.dt_end:%Y-%m-%d}  {b.start_fx.price:.0f}→{b.end_fx.price:.0f}  ({b.change_pct:+.1f}%)  bars={b.bar_count}")

print("\n=== 中枢 ===")
for z in res.zhongshus:
    print(f"  ZG={z.zg:.0f} ZD={z.zd:.0f} ZZ={z.zz:.0f}  {z.dt_start:%Y-%m-%d}~{z.dt_end:%Y-%m-%d}  (笔{z.start_bi_idx}~{z.end_bi_idx})")

closes = [b.close for b in res.std_bars]
divs = analyze_divergence(res.bis, closes)
print("\n=== 背驰信号 ===")
for d in divs:
    print(f"  {d['type']} {d['date']} @{d['price']} ratio={d['ratio']}")

print(f"\n最新收盘: {df['Close'].iloc[-1]:.0f} ({df['Date'].iloc[-1]:%Y-%m-%d})")
