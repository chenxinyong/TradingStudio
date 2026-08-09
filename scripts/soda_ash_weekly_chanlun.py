"""
纯碱指数周线缠论分析 (Soda Ash Weekly Chan Theory Analysis)
═══════════════════════════════════════════════════════════
缠论五步管线: 包含处理 → 分型识别 → 笔识别 → 中枢识别 → 背驰分析
数据源: akshare 郑商所纯碱连续合约 (SA0)
"""

import sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

import numpy as np
import pandas as pd
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches
from datetime import datetime, timedelta
from dataclasses import dataclass, field
from enum import Enum
from typing import List, Tuple
import warnings
warnings.filterwarnings('ignore')
import akshare as ak

# ═══════════════════════════════════════════════
# 参数配置 (对齐 ChanLunConfig.cs)
# ═══════════════════════════════════════════════
MIN_BI_LEN = 4          # 周线笔长度（纯碱上市仅7年，略放宽）
ZS_MIN_OVERLAP = 0.005  # 中枢最小重叠比例
DIVERGENCE_RATIO = 0.5  # 背驰力度比阈值
MACD_FAST, MACD_SLOW, MACD_SIGNAL = 12, 26, 9

# ═══════════════════════════════════════════════
# 数据结构
# ═══════════════════════════════════════════════
class Direction(Enum):
    UP = 1
    DOWN = -1

@dataclass
class Fractal:
    index: int
    type: str
    price: float
    date: datetime

@dataclass
class Bi:
    start_idx: int
    end_idx: int
    start_price: float
    end_price: float
    direction: Direction
    bars: int

@dataclass
class Zhongshu:
    start_idx: int
    end_idx: int
    high: float
    low: float
    bis_in: List[Bi] = field(default_factory=list)

# ═══════════════════════════════════════════════
# 数据获取
# ═══════════════════════════════════════════════
def fetch_soda_ash_weekly() -> pd.DataFrame:
    print("获取纯碱期货日线数据 (郑商所 SA0)...")
    df_daily = ak.futures_zh_daily_sina(symbol='SA0')
    df_daily = df_daily.rename(columns={'date': 'Date', 'open': 'Open', 'high': 'High',
                                         'low': 'Low', 'close': 'Close', 'volume': 'Volume'})
    df_daily['Date'] = pd.to_datetime(df_daily['Date'])
    df_daily = df_daily[['Date', 'Open', 'High', 'Low', 'Close', 'Volume']].dropna()
    print(f"  日线: {len(df_daily)} 根, {df_daily['Date'].iloc[0].strftime('%Y-%m-%d')} ~ {df_daily['Date'].iloc[-1].strftime('%Y-%m-%d')}")
    print(f"  价格区间: {df_daily['Low'].min():.0f} ~ {df_daily['High'].max():.0f}")

    # 重采样为周线
    df_daily = df_daily.set_index('Date')
    df_weekly = df_daily.resample('W-FRI').agg({
        'Open': 'first', 'High': 'max', 'Low': 'min', 'Close': 'last', 'Volume': 'sum'
    }).dropna().reset_index()

    print(f"  周线: {len(df_weekly)} 根, {df_weekly['Date'].iloc[0].strftime('%Y-%m-%d')} ~ {df_weekly['Date'].iloc[-1].strftime('%Y-%m-%d')}")
    return df_weekly

# ═══════════════════════════════════════════════
# Step 1: 包含处理
# ═══════════════════════════════════════════════
def process_inclusion(df: pd.DataFrame) -> pd.DataFrame:
    bars = df.copy().reset_index(drop=True)
    result = [bars.iloc[0].to_dict()]
    direction = None
    for i in range(1, len(bars)):
        prev = result[-1]
        curr = bars.iloc[i].to_dict()
        prev_r = prev['High'] - prev['Low']
        curr_r = curr['High'] - curr['Low']
        if prev_r <= 0 or curr_r <= 0:
            result.append(curr); continue
        prev_contains = (prev['High'] >= curr['High'] and prev['Low'] <= curr['Low'])
        curr_contains = (curr['High'] >= prev['High'] and curr['Low'] <= prev['Low'])
        if prev_contains or curr_contains:
            if direction is None:
                direction = 'up' if len(result) >= 2 and prev['High'] > result[-2]['High'] else \
                           ('down' if len(result) >= 2 else ('up' if curr['Close'] > prev['Close'] else 'down'))
            if direction == 'up':
                merged = {'Date': curr['Date'], 'Open': prev['Open'],
                          'High': max(prev['High'], curr['High']),
                          'Low': max(prev['Low'], curr['Low']),
                          'Close': curr['Close'], 'Volume': prev['Volume'] + curr['Volume']}
            else:
                merged = {'Date': curr['Date'], 'Open': prev['Open'],
                          'High': min(prev['High'], curr['High']),
                          'Low': min(prev['Low'], curr['Low']),
                          'Close': curr['Close'], 'Volume': prev['Volume'] + curr['Volume']}
            result[-1] = merged
        else:
            result.append(curr)
            direction = 'up' if curr['High'] > prev['High'] else ('down' if curr['Low'] < prev['Low'] else direction)
    return pd.DataFrame(result)

# ═══════════════════════════════════════════════
# Step 2: 分型识别
# ═══════════════════════════════════════════════
def detect_fractals(df: pd.DataFrame) -> List[Fractal]:
    fractals = []
    for i in range(1, len(df) - 1):
        h_p, h_c, h_n = df['High'].iloc[i-1], df['High'].iloc[i], df['High'].iloc[i+1]
        l_p, l_c, l_n = df['Low'].iloc[i-1], df['Low'].iloc[i], df['Low'].iloc[i+1]
        if h_c > h_p and h_c > h_n:
            fractals.append(Fractal(i, 'Top', h_c, df['Date'].iloc[i]))
        elif l_c < l_p and l_c < l_n:
            fractals.append(Fractal(i, 'Bottom', l_c, df['Date'].iloc[i]))
    # 去重
    cleaned, i = [], 0
    while i < len(fractals):
        j, best = i + 1, fractals[i]
        while j < len(fractals) and fractals[j].type == best.type:
            if (best.type == 'Top' and fractals[j].price > best.price) or \
               (best.type == 'Bottom' and fractals[j].price < best.price):
                best = fractals[j]
            j += 1
        cleaned.append(best); i = j
    return cleaned

# ═══════════════════════════════════════════════
# Step 3: 笔识别
# ═══════════════════════════════════════════════
def build_bis(fractals: List[Fractal], df: pd.DataFrame, min_bi_len: int = MIN_BI_LEN) -> List[Bi]:
    if len(fractals) < 2: return []
    bis, i = [], 0
    while i < len(fractals) - 1:
        f1, f2 = fractals[i], fractals[i+1]
        if f1.type == f2.type: i += 1; continue
        bc = f2.index - f1.index
        if bc < min_bi_len: i += 1; continue
        valid = True
        if f1.type == 'Bottom' and f2.type == 'Top':
            for k in range(f1.index, f2.index + 1):
                if df['Low'].iloc[k] < f1.price or df['High'].iloc[k] > f2.price:
                    valid = False; break
        else:
            for k in range(f1.index, f2.index + 1):
                if df['High'].iloc[k] > f1.price or df['Low'].iloc[k] < f2.price:
                    valid = False; break
        if valid:
            d = Direction.UP if f1.type == 'Bottom' else Direction.DOWN
            bis.append(Bi(f1.index, f2.index, f1.price, f2.price, d, bc))
        i += 1
    return bis

# ═══════════════════════════════════════════════
# Step 4: 中枢识别
# ═══════════════════════════════════════════════
def build_zhongshus(bis: List[Bi], min_overlap: float = ZS_MIN_OVERLAP) -> List[Zhongshu]:
    if len(bis) < 3: return []
    zhongshus, i = [], 0
    while i <= len(bis) - 3:
        b1, b2, b3 = bis[i], bis[i+1], bis[i+2]
        if b1.direction == b2.direction or b2.direction == b3.direction:
            i += 1; continue
        highs, lows = [], []
        for b in [b1, b2, b3]:
            highs.append(max(b.start_price, b.end_price))
            lows.append(min(b.start_price, b.end_price))
        zg, zd = min(highs), max(lows)
        if zg > zd + min_overlap * zd:
            zs = Zhongshu(b1.start_idx, b3.end_idx, zg, zd, [b1, b2, b3])
            j = i + 3
            while j < len(bis):
                bj = bis[j]; bj_h = max(bj.start_price, bj.end_price); bj_l = min(bj.start_price, bj.end_price)
                if bj_l < zg and bj_h > zd:
                    zs.bis_in.append(bj); zs.end_idx = bj.end_idx
                    highs.append(bj_h); lows.append(bj_l)
                    zg, zd = min(highs), max(lows); zs.high, zs.low = zg, zd
                    j += 1
                else: break
            zhongshus.append(zs); i = j
        else: i += 1
    return zhongshus

# ═══════════════════════════════════════════════
# Step 5: 背驰 & MACD
# ═══════════════════════════════════════════════
def calc_macd(df: pd.DataFrame) -> Tuple[np.ndarray, np.ndarray, np.ndarray]:
    close = df['Close'].values
    ema_fast = pd.Series(close).ewm(span=MACD_FAST, adjust=False).mean().values
    ema_slow = pd.Series(close).ewm(span=MACD_SLOW, adjust=False).mean().values
    dif = ema_fast - ema_slow
    dea = pd.Series(dif).ewm(span=MACD_SIGNAL, adjust=False).mean().values
    macd = 2 * (dif - dea)
    return dif, dea, macd

def analyze_divergence(bis: List[Bi], df: pd.DataFrame) -> List[dict]:
    if len(bis) < 2: return []
    dif, _, _ = calc_macd(df)
    signals = []
    for i in range(1, len(bis)):
        pb, cb = bis[i-1], bis[i]
        if pb.direction != cb.direction: continue
        def area(s, e):
            a = sum(abs(dif[k]) for k in range(s, e + 1))
            return max(a, 1e-10)
        pa, ca = area(pb.start_idx, pb.end_idx), area(cb.start_idx, cb.end_idx)
        ratio = ca / pa
        if cb.direction == Direction.UP and cb.end_price > pb.end_price and ratio < DIVERGENCE_RATIO:
            signals.append({'type': '顶背驰 (1卖)', 'date': df['Date'].iloc[cb.end_idx],
                           'price': cb.end_price, 'force_ratio': ratio})
        elif cb.direction == Direction.DOWN and cb.end_price < pb.end_price and ratio < DIVERGENCE_RATIO:
            signals.append({'type': '底背驰 (1买)', 'date': df['Date'].iloc[cb.end_idx],
                           'price': cb.end_price, 'force_ratio': ratio})
    return signals

def identify_points(bis: List[Bi], zhongshus: List[Zhongshu], df: pd.DataFrame, signals: List[dict]) -> List[dict]:
    points = []
    for sig in signals:
        pts = []
        if '底背驰' in sig['type']:
            pts.append({'type': '一买(B1)', 'date': sig['date'], 'price': sig['price']})
        elif '顶背驰' in sig['type']:
            pts.append({'type': '一卖(S1)', 'date': sig['date'], 'price': sig['price']})
        points.extend(pts)
    for zs in zhongshus:
        for j, bi in enumerate(bis):
            if bi.end_idx < zs.end_idx: continue
            if bi.direction == Direction.DOWN:
                bi_low = min(bi.start_price, bi.end_price)
                if zs.low * 0.97 <= bi_low <= zs.low * 1.03:
                    points.append({'type': '二买(B2)', 'date': df['Date'].iloc[bi.end_idx], 'price': bi.end_price})
                elif bi_low > zs.high:
                    points.append({'type': '三买(B3)', 'date': df['Date'].iloc[bi.end_idx], 'price': bi.end_price})
            else:
                bi_high = max(bi.start_price, bi.end_price)
                if zs.high * 0.97 <= bi_high <= zs.high * 1.03:
                    points.append({'type': '二卖(S2)', 'date': df['Date'].iloc[bi.end_idx], 'price': bi.end_price})
                elif bi_high < zs.low:
                    points.append({'type': '三卖(S3)', 'date': df['Date'].iloc[bi.end_idx], 'price': bi.end_price})
    return points

# ═══════════════════════════════════════════════
# 可视化
# ═══════════════════════════════════════════════
def plot_chanlun(df_std: pd.DataFrame, fractals: List[Fractal], bis: List[Bi],
                 zhongshus: List[Zhongshu], signals: List[dict], points: List[dict]):
    plt.rcParams['font.sans-serif'] = ['Microsoft YaHei', 'SimHei', 'DejaVu Sans']
    plt.rcParams['axes.unicode_minus'] = False

    fig, axes = plt.subplots(3, 1, figsize=(24, 15),
                              gridspec_kw={'height_ratios': [4, 1, 1]}, sharex=True)
    ax1, ax2, ax3 = axes
    dates, closes = df_std['Date'], df_std['Close'].values
    opens = df_std['Open'].values
    n = len(df_std)
    w = max(0.4, 1.0 * 7 / n)

    # K线
    colors_k = ['#26a69a' if closes[i] >= opens[i] else '#ef5350' for i in range(n)]
    ax1.bar(range(n), df_std['High'] - df_std['Low'], bottom=df_std['Low'],
            color=colors_k, width=w, alpha=0.75)
    ax1.bar(range(n), abs(closes - opens), bottom=np.minimum(closes, opens),
            color=colors_k, width=w * 1.8)

    # 分型
    for f in fractals:
        c, m = ('#ef5350', 'v') if f.type == 'Top' else ('#26a69a', '^')
        ax1.scatter(f.index, f.price, c=c, marker=m, s=60, zorder=5, edgecolors='white', linewidths=0.5)

    # 笔
    for bi in bis:
        c = '#ef5350' if bi.direction == Direction.UP else '#26a69a'
        ax1.plot([bi.start_idx, bi.end_idx], [bi.start_price, bi.end_price],
                color=c, linewidth=2.2, alpha=0.65, zorder=3)

    # 中枢
    for j, zs in enumerate(zhongshus):
        rect = mpatches.Rectangle((zs.start_idx - 0.3, zs.low),
                                  zs.end_idx - zs.start_idx + 0.6, zs.high - zs.low,
                                  facecolor='#7e57c2', alpha=0.18, edgecolor='#5e35b1',
                                  linewidth=1.3, linestyle='--', zorder=2)
        ax1.add_patch(rect)
        mid_x = (zs.start_idx + zs.end_idx) / 2
        pct = 100 * (zs.high - zs.low) / zs.low
        ax1.annotate(f'Z{j+1}\n{zs.low:.0f}-{zs.high:.0f}', xy=(mid_x, zs.high),
                    fontsize=6.5, color='#5e35b1', ha='center', va='bottom', fontweight='bold')

    # 买卖点
    pc = {'一买(B1)': '#00c853', '二买(B2)': '#00e676', '三买(B3)': '#69f0ae',
          '一卖(S1)': '#d50000', '二卖(S2)': '#ff1744', '三卖(S3)': '#ff5252'}
    for p in points:
        idx = df_std[df_std['Date'] == p['date']].index
        if len(idx) == 0: continue
        c = pc.get(p['type'], 'black')
        ax1.scatter(idx[0], p['price'], c=c, marker='o', s=140, zorder=6,
                   edgecolors='white', linewidths=1.5)

    # 背驰标注
    for sig in signals:
        idx = df_std[df_std['Date'] == sig['date']].index
        if len(idx) == 0: continue
        idx = idx[0]
        is_buy = '买' in sig['type']
        c, off = ('#00c853', 3) if is_buy else ('#d50000', -3)
        ax1.annotate(f"{sig['type']}\n{sig['price']:.0f} r={sig['force_ratio']:.2f}",
                    xy=(idx, sig['price']), xytext=(idx + 4, sig['price'] * (1 + off/100)),
                    fontsize=7, color=c, fontweight='bold',
                    arrowprops=dict(arrowstyle='->', color=c, lw=1),
                    bbox=dict(boxstyle='round,pad=0.2', facecolor='white', alpha=0.85))

    # MACD
    dif, dea, macd = calc_macd(df_std)
    x = range(n)
    ax2.plot(x, dif, color='#1a237e', linewidth=1, label='DIF')
    ax2.plot(x, dea, color='#b71c1c', linewidth=1, label='DEA')
    ax2.bar(x, macd, color=['#26a69a' if v >= 0 else '#ef5350' for v in macd],
            width=w, alpha=0.65)
    ax2.axhline(y=0, color='gray', linewidth=0.5)
    ax2.set_ylabel('MACD', fontsize=10)
    ax2.legend(loc='upper left', fontsize=8)
    ax2.grid(axis='y', alpha=0.3)

    # 成交量
    ax3.bar(x, df_std['Volume'].values, color=colors_k, width=w, alpha=0.55)
    ax3.set_ylabel('Volume', fontsize=10)
    ax3.grid(axis='y', alpha=0.3)

    # X轴
    step = max(1, n // 22)
    ticks = list(range(0, n, step))
    ax3.set_xticks(ticks)
    ax3.set_xticklabels([dates.iloc[t].strftime('%Y-%m') for t in ticks],
                         rotation=45, ha='right', fontsize=7.5)

    ax1.set_ylabel('Price (元/吨)', fontsize=11)
    ax1.set_title('纯碱连续合约 周线缠论分析 (SA Weekly — Chan Theory)',
                  fontsize=14, fontweight='bold', pad=15)
    ax1.grid(axis='y', alpha=0.3)
    ax1.legend(handles=[
        mpatches.Patch(color='#ef5350', label='上升笔'),
        mpatches.Patch(color='#26a69a', label='下降笔'),
        mpatches.Patch(color='#7e57c2', alpha=0.25, label='中枢'),
        plt.Line2D([0], [0], marker='v', color='w', markerfacecolor='#ef5350', markersize=8, label='顶分型'),
        plt.Line2D([0], [0], marker='^', color='w', markerfacecolor='#26a69a', markersize=8, label='底分型'),
    ], loc='upper left', fontsize=8)

    plt.tight_layout()
    out = 'scripts/soda_ash_weekly_chanlun.png'
    plt.savefig(out, dpi=150, bbox_inches='tight', facecolor='white')
    print(f"\n图表已保存: {out}")
    plt.close()

# ═══════════════════════════════════════════════
# 主流程
# ═══════════════════════════════════════════════
def main():
    print("=" * 60)
    print("  纯碱周线缠论分析 (Chan Theory on Soda Ash Weekly)")
    print("=" * 60)

    df = fetch_soda_ash_weekly()

    # Step 1: 包含处理
    df_std = process_inclusion(df)
    print(f"\nStep 1 - 包含处理: {len(df)} -> {len(df_std)} 根 ({len(df) - len(df_std)} 合并)")

    # Step 2: 分型
    fractals = detect_fractals(df_std)
    print(f"Step 2 - 分型: {len(fractals)} (顶:{sum(1 for f in fractals if f.type=='Top')}, 底:{sum(1 for f in fractals if f.type=='Bottom')})")

    # Step 3: 笔
    bis = build_bis(fractals, df_std, min_bi_len=MIN_BI_LEN)
    print(f"Step 3 - 笔: {len(bis)} (上升:{sum(1 for b in bis if b.direction==Direction.UP)}, 下降:{sum(1 for b in bis if b.direction==Direction.DOWN)})")

    # Step 4: 中枢
    zhongshus = build_zhongshus(bis)
    print(f"Step 4 - 中枢: {len(zhongshus)}")

    # Step 5: 背驰
    signals = analyze_divergence(bis, df_std)
    print(f"Step 5 - 背驰: {len(signals)}")

    points = identify_points(bis, zhongshus, df_std, signals)
    print(f"买卖点: {len([p for p in points if '买' in p['type']])}买, {len([p for p in points if '卖' in p['type']])}卖")

    # 中枢详情
    print("\n" + "-" * 55)
    print("中枢详情:")
    for i, zs in enumerate(zhongshus):
        sd = df_std['Date'].iloc[zs.start_idx]; ed = df_std['Date'].iloc[zs.end_idx]
        w = 100 * (zs.high - zs.low) / zs.low
        print(f"  Z{i+1}: {sd.strftime('%Y-%m-%d')} ~ {ed.strftime('%Y-%m-%d')} | "
              f"{zs.low:.0f}-{zs.high:.0f} (宽{w:.1f}%) | {len(zs.bis_in)}笔")

    # 背驰信号
    if signals:
        print("\n" + "-" * 55)
        print("背驰信号:")
        for s in signals:
            print(f"  {s['type']} @ {s['date'].strftime('%Y-%m-%d')} | {s['price']:.0f} | r={s['force_ratio']:.2f}")

    # 当前状态
    print("\n" + "=" * 55)
    print("当前状态研判:")
    print("=" * 55)
    last_close = df_std['Close'].iloc[-1]
    last_date = df_std['Date'].iloc[-1]

    if bis:
        lb = bis[-1]
        ds = '上升' if lb.direction == Direction.UP else '下降'
        sd = df_std['Date'].iloc[lb.start_idx]; ed = df_std['Date'].iloc[lb.end_idx]
        pct = (lb.end_price / lb.start_price - 1) * 100
        print(f"  最后一笔: {ds}笔 ({sd.strftime('%Y-%m-%d')} -> {ed.strftime('%Y-%m-%d')})")
        print(f"            {lb.start_price:.0f} -> {lb.end_price:.0f} ({pct:+.1f}%)")
    print(f"  最新收盘: {last_date.strftime('%Y-%m-%d')} = {last_close:.0f}")

    if zhongshus:
        lz = zhongshus[-1]
        if last_close > lz.high:
            print(f"  Z{len(zhongshus)} 上方 {100*(last_close/lz.high-1):.1f}% | 中枢={lz.low:.0f}-{lz.high:.0f}")
            print(f"  -> 中枢上方运行, 关注三买/三卖机会")
        elif last_close < lz.low:
            print(f"  Z{len(zhongshus)} 下方 {100*(1-last_close/lz.low):.1f}% | 中枢={lz.low:.0f}-{lz.high:.0f}")
            print(f"  -> 中枢下方运行")
        else:
            print(f"  Z{len(zhongshus)} 内部 | 中枢={lz.low:.0f}-{lz.high:.0f}")
            print(f"  -> 中枢震荡中")

    # 关键价位
    print(f"\n  关键价位:")
    print(f"    历史高点: {df['High'].max():.0f}")
    print(f"    历史低点: {df['Low'].min():.0f}")
    print(f"    最新中枢: {zhongshus[-1].low:.0f}-{zhongshus[-1].high:.0f}" if zhongshus else "    无中枢")

    if signals:
        ls = signals[-1]
        print(f"  最近背驰: {ls['type']} @ {ls['date'].strftime('%Y-%m-%d')} {ls['price']:.0f} (r={ls['force_ratio']:.2f})")

    # 画图
    plot_chanlun(df_std, fractals, bis, zhongshus, signals, points)
    print("\n分析完成")

if __name__ == '__main__':
    main()
