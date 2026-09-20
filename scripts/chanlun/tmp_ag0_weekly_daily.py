# -*- coding: utf-8 -*-
"""临时脚本：白银 AG0 连续主力 周线+日线 缠论分析。"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass
import akshare as ak
import pandas as pd
from chanlun_core import Bar, analyze

SYM = "AG0"

def fetch():
    df = ak.futures_zh_daily_sina(symbol=SYM)
    df = df.rename(columns=str.lower)
    df["date"] = pd.to_datetime(df["date"])
    return df

def agg(df, freq):
    df = df.copy()
    df["key"] = df["date"].dt.strftime("%G-W%V")  # ISO 周
    g = df.groupby("key", sort=True)
    out = []
    for k, sub in g:
        sub = sub.sort_values("date")
        out.append({
            "date": sub["date"].iloc[-1],
            "open": sub["open"].iloc[0],
            "high": sub["high"].max(),
            "low": sub["low"].min(),
            "close": sub["close"].iloc[-1],
        })
    return out

def bars_to_chan(rows):
    return [Bar(dt=r["date"], open=r["open"], high=r["high"],
                low=r["low"], close=r["close"], vol=0) for r in rows]

def show(label, rows, min_bi_len):
    bars = bars_to_chan(rows)
    r = analyze(bars, min_bi_len=min_bi_len, min_zs_overlap=0.0)
    print(f"\n===== {label}  {len(rows)}根  笔{len(r.bis)}  中枢{len(r.zhongshus)}  走势={r.trend} =====")
    # 只打印最近 12 笔，太长截断
    start = max(0, len(r.bis) - 12)
    for i in range(start, len(r.bis)):
        bi = r.bis[i]
        d = "UP↑" if bi.end_fx.price > bi.start_fx.price else "DN↓"
        print(f"  Bi{i+1}/{len(r.bis)} {d} {bi.dt_start.strftime('%Y-%m-%d')}({bi.start_fx.price:.0f}) -> "
              f"{bi.dt_end.strftime('%Y-%m-%d')}({bi.end_fx.price:.0f})  {bi.change_pct:+.1f}%")
    for i, zs in enumerate(r.zhongshus):
        print(f"  ZS{i+1}: [{zs.zd:.0f} ~ {zs.zg:.0f}]  {zs.dt_start.strftime('%Y-%m')} ~ {zs.dt_end.strftime('%Y-%m')}")
    last = bars[-1].close
    if r.zhongshus:
        z = r.zhongshus[-1]
        if last > z.zg: loc = "ABOVE(三买区)"
        elif last < z.zd: loc = f"BELOW(三卖区 {z.zd-last:.0f})"
        else: loc = "INSIDE(中枢震荡)"
        print(f"  现价 {last:.0f} 相对最近中枢[{z.zd:.0f}~{z.zg:.0f}] -> {loc}")
    else:
        print(f"  现价 {last:.0f}，无中枢")
    if r.fractals:
        tops = [f for f in r.fractals if f.type.value == "TOP"]
        bots = [f for f in r.fractals if f.type.value == "BOTTOM"]
        if tops: print(f"  最近顶分型: {tops[-1].dt.strftime('%Y-%m-%d')} H={tops[-1].price:.0f}")
        if bots: print(f"  最近底分型: {bots[-1].dt.strftime('%Y-%m-%d')} L={bots[-1].price:.0f}")

if __name__ == "__main__":
    df = fetch()
    print(f"数据: {df['date'].iloc[0].date()} ~ {df['date'].iloc[-1].date()}  共{len(df)}根日K")
    print(f"最新收盘 {df['close'].iloc[-1]:.0f}  区间 高{df['high'].max():.0f} 低{df['low'].min():.0f}")

    weekly = agg(df, "W")
    daily = df[["date","open","high","low","close"]].sort_values("date").to_dict("records")

    show("周线(全历史)", weekly, min_bi_len=3)
    daily_recent = [r for r in daily if r["date"] >= pd.Timestamp("2025-06-01")]
    show("日线(2025-06至今)", daily_recent, min_bi_len=4)

    print("\n===== 近 15 根日K =====")
    for r in daily[-15:]:
        print(f"  {r['date'].strftime('%Y-%m-%d')}  O{r['open']:.0f} H{r['high']:.0f} L{r['low']:.0f} C{r['close']:.0f}")
