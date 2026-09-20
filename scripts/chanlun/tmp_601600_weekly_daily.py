# -*- coding: utf-8 -*-
"""临时脚本：601600 中国铝业 周线+日线 缠论分析（不复权）。"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass
import akshare as ak
import pandas as pd
from chanlun_core import Bar, analyze

SYM = "sh601600"

def fetch():
    df = ak.stock_zh_a_daily(symbol=SYM, adjust="")  # 不复权（铁律）
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
    for i, bi in enumerate(r.bis):
        d = "UP" if bi.type.value == "Up" else "DN"
        print(f"  Bi{i+1}/{len(r.bis)} {d} {bi.dt_start.strftime('%Y-%m-%d')}({bi.start_fx.price:.2f}) -> "
              f"{bi.dt_end.strftime('%Y-%m-%d')}({bi.end_fx.price:.2f})  {bi.change_pct:+.1f}%")
    for i, zs in enumerate(r.zhongshus):
        print(f"  ZS{i+1}: [{zs.zd:.2f} ~ {zs.zg:.2f}]  {zs.dt_start.strftime('%Y-%m')} ~ {zs.dt_end.strftime('%Y-%m')}")
    last = bars[-1].close
    if r.zhongshus:
        z = r.zhongshus[-1]
        if last > z.zg: loc = "ABOVE(三买区)"
        elif last < z.zd: loc = f"BELOW(三卖区 {z.zd-last:.2f})"
        else: loc = "INSIDE(中枢震荡)"
        print(f"  现价 {last:.2f} 相对最近中枢[{z.zd:.2f}~{z.zg:.2f}] -> {loc}")
    else:
        print(f"  现价 {last:.2f}，无中枢")
    if r.fractals:
        tops = [f for f in r.fractals if f.type.value == "TOP"]
        bots = [f for f in r.fractals if f.type.value == "BOTTOM"]
        if tops: print(f"  最近顶分型: {tops[-1].dt.strftime('%Y-%m-%d')} H={tops[-1].price:.2f}")
        if bots: print(f"  最近底分型: {bots[-1].dt.strftime('%Y-%m-%d')} L={bots[-1].price:.2f}")

if __name__ == "__main__":
    df = fetch()
    print(f"数据: {df['date'].iloc[0].date()} ~ {df['date'].iloc[-1].date()}  共{len(df)}根日K")
    print(f"最新收盘 {df['close'].iloc[-1]:.2f}  区间 高{df['high'].max():.2f} 低{df['low'].min():.2f}")

    weekly = agg(df, "W")
    daily = df[["date","open","high","low","close"]].sort_values("date").to_dict("records")

    show("周线(全历史)", weekly, min_bi_len=3)
    daily_recent = [r for r in daily if r["date"] >= pd.Timestamp("2025-01-01")]
    show("日线(2025至今)", daily_recent, min_bi_len=4)

    # 近 30 根日K（定位当前三段式）
    print("\n===== 近 30 根日K =====")
    for r in daily[-30:]:
        print(f"  {r['date'].strftime('%Y-%m-%d')}  O{r['open']:.2f} H{r['high']:.2f} L{r['low']:.2f} C{r['close']:.2f}")
