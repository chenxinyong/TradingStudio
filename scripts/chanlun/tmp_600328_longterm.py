# -*- coding: utf-8 -*-
"""临时脚本：新强联 300850 中长期（月线+周线+日线）缠论分析。
拉前复权完整历史 → 聚合月/周/日 → 用 chanlun_core 跑缠论 → 打印结构。"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import akshare as ak
from chanlun_core import Bar, analyze

SYM = "sh600328"

def fetch_qfq():
    df = ak.stock_zh_a_daily(symbol=SYM, adjust="qfq")
    df = df.rename(columns=str.lower)
    df["date"] = pd_to_datetime(df)
    return df

def pd_to_datetime(df):
    import pandas as pd
    return pd.to_datetime(df["date"])

def agg(df, freq):
    """freq: 'M' 月线 / 'W' 周线(按 ISO) """
    import pandas as pd
    df = df.copy()
    if freq == "M":
        df["key"] = df["date"].dt.strftime("%Y-%m")
    else:
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
    print(f"\n===== {label}   {len(rows)}根  笔{len(r.bis)}  中枢{len(r.zhongshus)}  走势={r.trend} =====")
    for i, bi in enumerate(r.bis):
        d = "↑涨" if bi.type.value == "Up" else "↓跌"
        print(f"  Bi{i+1} {d} {bi.dt_start.strftime('%Y-%m-%d')}({bi.start_fx.price:.2f}) -> "
              f"{bi.dt_end.strftime('%Y-%m-%d')}({bi.end_fx.price:.2f})  {bi.change_pct:+.1f}%")
    for i, zs in enumerate(r.zhongshus):
        print(f"  ZS{i+1}: [{zs.zd:.2f} ~ {zs.zg:.2f}]  {zs.dt_start.strftime('%Y-%m')} ~ {zs.dt_end.strftime('%Y-%m')}")
    last = bars[-1].close
    if r.zhongshus:
        z = r.zhongshus[-1]
        if last > z.zg: loc = "三买区(中枢上方)"
        elif last < z.zd: loc = f"三卖区(中枢下方 {z.zd-last:.2f})"
        else: loc = "中枢震荡"
        print(f"  现价 {last:.2f} 相对最近中枢[{z.zd:.2f}~{z.zg:.2f}] → {loc}")
    else:
        print(f"  现价 {last:.2f}，无中枢")
    if r.fractals:
        tops = [f for f in r.fractals if f.type.value == "TOP"]
        bots = [f for f in r.fractals if f.type.value == "BOTTOM"]
        if tops: print(f"  最近顶分型: {tops[-1].dt.strftime('%Y-%m-%d')} H={tops[-1].price:.2f}")
        if bots: print(f"  最近底分型: {bots[-1].dt.strftime('%Y-%m-%d')} L={bots[-1].price:.2f}")

if __name__ == "__main__":
    df = fetch_qfq()
    print(f"数据: {df['date'].iloc[0].date()} ~ {df['date'].iloc[-1].date()}  共{len(df)}根日K")
    print(f"区间: 高{df['high'].max():.2f} 低{df['low'].min():.2f}  最新收盘{df['close'].iloc[-1]:.2f}")
    monthly = agg(df, "M")
    weekly = agg(df, "W")
    daily = df[["date","open","high","low","close"]].sort_values("date").to_dict("records")
    show("月线", monthly, min_bi_len=2)
    show("周线", weekly, min_bi_len=3)
    show("日线(近3年)", daily[-750:], min_bi_len=5)
    print("\n===== 近12根月K =====")
    for r in monthly[-12:]:
        print(f"  {r['date'].strftime('%Y-%m')}  O{r['open']:.2f} H{r['high']:.2f} L{r['low']:.2f} C{r['close']:.2f}")
