#!/usr/bin/env python3
"""688185 康希诺 — 缠论结构 + 五均线系统分析（不复权口径）

口径铁律（见 memory: chanlun-methodology）：
  1. 不复权（真实价），与持仓成本/现价口径一致
  2. 以「本轮走势」为独立分析单元，不跨越顶部引用上涨段的旧价位
  3. 反弹级别靠内部结构判断，不看到没到静态价位
  4. 五条均线 5/13/34/89/233 辅助：34=日线级、89=周线级、233=月线级

输出：周线 + 日线的 笔/中枢/走势分类、关键分型、五均线位置
"""
import sys, os, datetime
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "chanlun"))
sys.stdout.reconfigure(encoding="utf-8")

import akshare as ak
import warnings
warnings.filterwarnings("ignore")

from chanlun_core import Bar, analyze


def fetch(symbol="sh688185", adjust="qfq"):
    df = ak.stock_zh_a_daily(symbol=symbol, adjust=adjust)
    df["d"] = df["date"].astype(str)
    return [
        Bar(dt=datetime.datetime.strptime(r["d"], "%Y-%m-%d"),
            open=float(r["open"]), high=float(r["high"]),
            low=float(r["low"]), close=float(r["close"]), vol=int(r["volume"]))
        for _, r in df.iterrows()
    ]


def to_weekly(bars):
    """日线聚合周线（按 ISO 周）"""
    g = {}
    for b in bars:
        y, w, _ = b.dt.isocalendar()
        g.setdefault((y, w), []).append(b)
    out = []
    for k in sorted(g):
        grp = g[k]
        out.append(Bar(dt=grp[-1].dt, open=grp[0].open,
                       high=max(x.high for x in grp), low=min(x.low for x in grp),
                       close=grp[-1].close, vol=sum(x.vol for x in grp)))
    return out


def report(title, bars, top_n=6):
    r = analyze(bars)
    print(f"\n{'='*76}\n  {title}\n{'='*76}")
    print(f"  原始 {len(bars)} 根 → 包含处理后 {len(r.std_bars)} 根")
    print(f"  分型 {len(r.fractals)}  笔 {len(r.bis)}  中枢 {len(r.zhongshus)}")
    print(f"  走势分类：{r.trend}")
    print(f"  区间：{bars[0].dt:%Y-%m-%d} ~ {bars[-1].dt:%Y-%m-%d}   最新收 {bars[-1].close:.2f}")

    print(f"\n  最近 {top_n} 笔：")
    for b in r.bis[-top_n:]:
        d = "↑涨" if b.type.name == "Up" else "↓跌"
        print(f"    {b.dt_start:%Y-%m-%d}({b.start_fx.price:.2f}) → {b.dt_end:%Y-%m-%d}({b.end_fx.price:.2f})"
              f"  {d}  {b.change_pct:+.1f}%   {b.bar_count}根K")

    if r.zhongshus:
        z = r.zhongshus[-1]
        dt = f"{z.dt_start:%Y-%m-%d} ~ {z.dt_end:%Y-%m-%d}" if z.dt_start else "—"
        print(f"\n  最近中枢：下沿 {z.zd:.2f} ~ 上沿 {z.zg:.2f}（中轨 {z.zz:.2f}，{z.bi_count} 笔，{dt}）")
        c = bars[-1].close
        if c > z.zg:
            print(f"    现价 {c:.2f} 在中枢【上方】三买区，高出 {(c-z.zg)/z.zg*100:+.1f}%")
        elif c < z.zd:
            print(f"    现价 {c:.2f} 在中枢【下方】三卖区，低于 {(z.zd-c)/z.zd*100:.1f}%")
        else:
            print(f"    现价 {c:.2f} 在中枢【内部】震荡")
    if r.fractals:
        fb = [f for f in r.fractals if f.type.name == "Bottom"][-2:]
        ft = [f for f in r.fractals if f.type.name == "Top"][-2:]
        print(f"\n  最近底分型：" + "  ".join(f"{f.dt:%m-%d} L={f.price:.2f}" for f in fb))
        print(f"  最近顶分型：" + "  ".join(f"{f.dt:%m-%d} H={f.price:.2f}" for f in ft))
    return r


def ma_system(bars):
    closes = [b.close for b in bars]
    print(f"\n{'='*76}\n  五均线系统（5/13/34/89/233）\n{'='*76}")
    vals = {}
    for p in (5, 13, 34, 89, 233):
        v = sum(closes[-p:]) / p if len(closes) >= p else None
        vals[p] = v
        if v:
            print(f"  MA{p:<4} {v:>9.2f}   现价 {closes[-1]:.2f} 相对 {(closes[-1]-v)/v*100:+6.1f}%"
                  f"   {'站上' if closes[-1] > v else '跌破'}")
    order = [vals[p] for p in (5, 13, 34, 89, 233) if vals[p]]
    if len(order) == 5:
        if all(order[i] > order[i+1] for i in range(4)):
            print("  → 多头排列（5>13>34>89>233）")
        elif all(order[i] < order[i+1] for i in range(4)):
            print("  → 空头排列（5<13<34<89<233）")
        else:
            print("  → 均线缠绕（盘整/中枢特征）")
    return vals


if __name__ == "__main__":
    d = fetch()
    w = to_weekly(d)
    print(f"日线 {len(d)} 根，周线 {len(w)} 根（不复权）")
    report("周线结构", w)
    ma_system(w)
    report("日线结构（近两年）", [b for b in d if b.dt >= datetime.datetime(2024, 9, 1)], top_n=8)
    ma_system(d)
