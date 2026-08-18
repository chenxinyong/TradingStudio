"""
白银周线缠论分析 (Silver Weekly Chan Theory Analysis)
═══════════════════════════════════════════════════════
缠论五步管线: 包含处理 → 分型识别 → 笔识别 → 中枢识别 → 背驰分析
参数对齐 TradingStudio.Strategy/ChanLun/ChanLunConfig.cs
"""

import sys
import io
# Fix Windows GBK encoding for emoji
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

import numpy as np
import pandas as pd
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches
from datetime import datetime, timedelta
from dataclasses import dataclass, field
from enum import Enum
from typing import List, Optional, Tuple
import warnings
warnings.filterwarnings('ignore')

# ═══════════════════════════════════════════════
# 0. 参数配置 (对齐 ChanLunConfig.cs)
# ═══════════════════════════════════════════════
MIN_BI_LEN = 5          # 最小笔长度 (周线可适当降低)
MAX_BI_NUM = 500
ZS_MIN_OVERLAP = 0.01   # 中枢最小重叠 (价格比例)
DIVERGENCE_RATIO = 0.5  # 背驰力度比阈值
MACD_FAST = 12
MACD_SLOW = 26
MACD_SIGNAL = 9

# ═══════════════════════════════════════════════
# 1. 数据获取 — 使用 akshare (国内可用)
# ═══════════════════════════════════════════════
def fetch_silver_weekly() -> pd.DataFrame:
    """获取白银期货周线数据 (COMEX XAG via akshare)"""
    import akshare as ak
    print("获取白银期货日线数据 (COMEX XAG)...")

    # akshare COMEX白银连续合约
    df_daily = ak.futures_foreign_hist(symbol='XAG')

    # 标准化列名
    df_daily = df_daily.rename(columns={
        'date': 'Date', 'open': 'Open', 'high': 'High',
        'low': 'Low', 'close': 'Close', 'volume': 'Volume'
    })
    df_daily['Date'] = pd.to_datetime(df_daily['Date'])
    df_daily = df_daily[['Date', 'Open', 'High', 'Low', 'Close', 'Volume']].dropna()

    print(f"  日线: {len(df_daily)} 根, {df_daily['Date'].iloc[0].strftime('%Y-%m-%d')} ~ {df_daily['Date'].iloc[-1].strftime('%Y-%m-%d')}")

    # 重采样为周线 (周五收盘)
    df_daily = df_daily.set_index('Date')
    df_weekly = df_daily.resample('W-FRI').agg({
        'Open': 'first',
        'High': 'max',
        'Low': 'min',
        'Close': 'last',
        'Volume': 'sum',
    }).dropna()

    # 至少需要一些数据
    df_weekly = df_weekly.reset_index()
    # 取最近 18 年数据 (2008年到当前)
    cutoff = pd.Timestamp.now() - pd.DateOffset(years=18)
    df_weekly = df_weekly[df_weekly['Date'] >= cutoff].reset_index(drop=True)

    print(f"  周线: {len(df_weekly)} 根, {df_weekly['Date'].iloc[0].strftime('%Y-%m-%d')} ~ {df_weekly['Date'].iloc[-1].strftime('%Y-%m-%d')}")
    return df_weekly

# ═══════════════════════════════════════════════
# 2. 缠论数据结构
# ═══════════════════════════════════════════════
class Direction(Enum):
    UP = 1
    DOWN = -1

@dataclass
class Fractal:
    index: int
    type: str       # 'Top' or 'Bottom'
    price: float
    date: datetime

@dataclass
class Bi:
    start_idx: int
    end_idx: int
    start_price: float
    end_price: float
    direction: Direction
    bars: int   # 包含处理后的K线数

@dataclass
class Zhongshu:
    start_idx: int
    end_idx: int
    high: float     # ZG 中枢高点
    low: float      # ZD 中枢低点
    bis_in: List[Bi] = field(default_factory=list)

# ═══════════════════════════════════════════════
# 3. Step 1: 包含处理
# ═══════════════════════════════════════════════
def process_inclusion(df: pd.DataFrame) -> pd.DataFrame:
    """
    包含处理: 相邻两根K线如果存在包含关系,按方向合并。
    上升趋势中: 取高高、高低 (High取max, Low取max)
    下降趋势中: 取低高、低低 (High取min, Low取min)
    """
    bars = df.copy().reset_index(drop=True)
    result = [bars.iloc[0].to_dict()]

    direction = None  # 当前方向 (None=未定)

    for i in range(1, len(bars)):
        prev = result[-1]
        curr = bars.iloc[i].to_dict()

        # 判断包含关系
        prev_range = prev['High'] - prev['Low']
        curr_range = curr['High'] - curr['Low']

        if prev_range <= 0 or curr_range <= 0:
            result.append(curr)
            continue

        # 前包含后? 后包含前?
        is_prev_contains_curr = (prev['High'] >= curr['High'] and prev['Low'] <= curr['Low'])
        is_curr_contains_prev = (curr['High'] >= prev['High'] and curr['Low'] <= prev['Low'])

        if is_prev_contains_curr or is_curr_contains_prev:
            # 确定处理方向
            if direction is None:
                # 用前两根非包含K线判断
                if len(result) >= 2:
                    prev2 = result[-2]
                    direction = 'up' if prev['High'] > prev2['High'] else 'down'
                else:
                    direction = 'up' if curr['Close'] > prev['Close'] else 'down'

            if direction == 'up':
                # 上升: 取高高(GG)、高低(GD)
                merged = {
                    'Date': curr['Date'],
                    'Open': prev['Open'],
                    'High': max(prev['High'], curr['High']),
                    'Low': max(prev['Low'], curr['Low']),
                    'Close': curr['Close'],
                    'Volume': prev['Volume'] + curr['Volume'],
                }
            else:
                # 下降: 取低高(DG)、低低(DD)
                merged = {
                    'Date': curr['Date'],
                    'Open': prev['Open'],
                    'High': min(prev['High'], curr['High']),
                    'Low': min(prev['Low'], curr['Low']),
                    'Close': curr['Close'],
                    'Volume': prev['Volume'] + curr['Volume'],
                }
            result[-1] = merged
        else:
            # 无包含关系,更新方向
            result.append(curr)
            if curr['High'] > prev['High']:
                direction = 'up'
            elif curr['Low'] < prev['Low']:
                direction = 'down'

    return pd.DataFrame(result)

# ═══════════════════════════════════════════════
# 4. Step 2: 分型识别
# ═══════════════════════════════════════════════
def detect_fractals(df: pd.DataFrame) -> List[Fractal]:
    """
    顶分型: 中间K线高点最高,左右两根低点可以任意 (简化: 中间High > 左High 且 中间High > 右High)
    底分型: 中间K线低点最低,左右两根高点可以任意 (简化: 中间Low < 左Low 且 中间Low < 右Low)
    严格定义: 顶分型的中间K线High是相邻三根中最高的,且左右K线的High都低于它
    """
    fractals = []
    for i in range(1, len(df) - 1):
        h_prev, h_curr, h_next = df['High'].iloc[i-1], df['High'].iloc[i], df['High'].iloc[i+1]
        l_prev, l_curr, l_next = df['Low'].iloc[i-1], df['Low'].iloc[i], df['Low'].iloc[i+1]

        # 顶分型
        if h_curr > h_prev and h_curr > h_next:
            fractals.append(Fractal(i, 'Top', h_curr, df['Date'].iloc[i]))

        # 底分型
        elif l_curr < l_prev and l_curr < l_next:
            fractals.append(Fractal(i, 'Bottom', l_curr, df['Date'].iloc[i]))

    # 去重: 连续同类型分型只保留最极端的
    cleaned = []
    i = 0
    while i < len(fractals):
        j = i + 1
        best = fractals[i]
        while j < len(fractals) and fractals[j].type == best.type:
            if best.type == 'Top' and fractals[j].price > best.price:
                best = fractals[j]
            elif best.type == 'Bottom' and fractals[j].price < best.price:
                best = fractals[j]
            j += 1
        cleaned.append(best)
        i = j

    return cleaned

# ═══════════════════════════════════════════════
# 5. Step 3: 笔识别
# ═══════════════════════════════════════════════
def build_bis(fractals: List[Fractal], df: pd.DataFrame,
              min_bi_len: int = MIN_BI_LEN) -> List[Bi]:
    """
    笔的定义: 相邻的顶分型和底分型之间的连线。
    约束: 顶底之间至少要有 min_bi_len 根K线（包含处理后）。
    且顶分型的最高价必须高于相邻底分型区间内的所有K线最高价（反之亦然）。
    """
    if len(fractals) < 2:
        return []

    bis = []
    i = 0
    while i < len(fractals) - 1:
        f1 = fractals[i]
        f2 = fractals[i + 1]

        # 必须是顶-底或底-顶交替
        if f1.type == f2.type:
            i += 1
            continue

        # K线数量约束
        bar_count = f2.index - f1.index
        if bar_count < min_bi_len:
            i += 1
            continue

        # 价格逻辑约束
        if f1.type == 'Bottom' and f2.type == 'Top':
            # 底→顶: 底分型的最低点必须低于区间内所有K线低点, 顶分型最高点必须高于区间内所有K线高点
            valid = True
            for k in range(f1.index, f2.index + 1):
                if df['Low'].iloc[k] < f1.price:
                    valid = False
                    break
                if df['High'].iloc[k] > f2.price:
                    valid = False
                    break
            if valid:
                bis.append(Bi(f1.index, f2.index, f1.price, f2.price, Direction.UP, bar_count))
                i += 1
            else:
                i += 1
        elif f1.type == 'Top' and f2.type == 'Bottom':
            valid = True
            for k in range(f1.index, f2.index + 1):
                if df['High'].iloc[k] > f1.price:
                    valid = False
                    break
                if df['Low'].iloc[k] < f2.price:
                    valid = False
                    break
            if valid:
                bis.append(Bi(f1.index, f2.index, f1.price, f2.price, Direction.DOWN, bar_count))
                i += 1
            else:
                i += 1

    return bis

# ═══════════════════════════════════════════════
# 6. Step 4: 中枢识别
# ═══════════════════════════════════════════════
def build_zhongshus(bis: List[Bi], min_overlap: float = ZS_MIN_OVERLAP) -> List[Zhongshu]:
    """
    中枢定义: 至少连续3笔的重叠区间。
    重叠区间 = [max(笔1低点, 笔2低点, 笔3低点), min(笔1高点, 笔2高点, 笔3高点)]
    中枢高点 ZG = min(所有笔的高点), 中枢低点 ZD = max(所有笔的低点)
    """
    if len(bis) < 3:
        return []

    zhongshus = []
    i = 0
    while i <= len(bis) - 3:
        b1, b2, b3 = bis[i], bis[i+1], bis[i+2]

        # 笔必须交替方向
        if b1.direction == b2.direction or b2.direction == b3.direction:
            i += 1
            continue

        # 计算三笔的高低点范围
        highs = []
        lows = []
        for b in [b1, b2, b3]:
            highs.append(max(b.start_price, b.end_price))
            lows.append(min(b.start_price, b.end_price))

        zg = min(highs)  # 中枢高点
        zd = max(lows)   # 中枢低点

        if zg > zd + min_overlap * zd:  # 有效重叠
            zs = Zhongshu(
                start_idx=b1.start_idx,
                end_idx=b3.end_idx,
                high=zg,
                low=zd,
                bis_in=[b1, b2, b3]
            )

            # 延伸: 如果后续笔仍与中枢重叠,纳入中枢
            j = i + 3
            while j < len(bis):
                bj = bis[j]
                bj_high = max(bj.start_price, bj.end_price)
                bj_low = min(bj.start_price, bj.end_price)
                # 判断是否有重叠
                if bj_low < zg and bj_high > zd:
                    zs.bis_in.append(bj)
                    zs.end_idx = bj.end_idx
                    # 更新中枢边界
                    highs.append(bj_high)
                    lows.append(bj_low)
                    zg = min(highs)
                    zd = max(lows)
                    zs.high = zg
                    zs.low = zd
                    j += 1
                else:
                    break

            zhongshus.append(zs)
            i = j
        else:
            i += 1

    return zhongshus

# ═══════════════════════════════════════════════
# 7. Step 5: 背驰分析 & 买卖点识别
# ═══════════════════════════════════════════════
def calc_macd(df: pd.DataFrame) -> Tuple[np.ndarray, np.ndarray, np.ndarray]:
    """计算MACD"""
    close = df['Close'].values
    ema_fast = pd.Series(close).ewm(span=MACD_FAST, adjust=False).mean().values
    ema_slow = pd.Series(close).ewm(span=MACD_SLOW, adjust=False).mean().values
    dif = ema_fast - ema_slow
    dea = pd.Series(dif).ewm(span=MACD_SIGNAL, adjust=False).mean().values
    macd = 2 * (dif - dea)
    return dif, dea, macd

def analyze_divergence(bis: List[Bi], df: pd.DataFrame) -> List[dict]:
    """
    背驰分析: 比较同方向相邻两笔的力度。
    力度 = MACD面积 (dif的累计值在笔区间内的积分)
    背驰条件: 价格创新高/低, 但MACD面积缩小到 < DIVERGENCE_RATIO
    """
    if len(bis) < 2:
        return []

    dif, dea, macd = calc_macd(df)
    signals = []

    for i in range(1, len(bis)):
        prev_bi = bis[i-1]
        curr_bi = bis[i]

        if prev_bi.direction != curr_bi.direction:
            continue  # 方向不同不比较

        # 计算MACD面积
        def macd_area(start, end):
            area = 0
            for k in range(start, end + 1):
                area += abs(dif[k]) * (1 if dif[k] > 0 else -1)
            return abs(area)

        prev_area = macd_area(prev_bi.start_idx, prev_bi.end_idx)
        curr_area = macd_area(curr_bi.start_idx, curr_bi.end_idx)

        if prev_area <= 0:
            continue

        ratio = curr_area / prev_area

        # 顶背驰: 方向向上, 价格新高但力度衰竭
        if curr_bi.direction == Direction.UP:
            if curr_bi.end_price > prev_bi.end_price and ratio < DIVERGENCE_RATIO:
                signals.append({
                    'type': '顶背驰 (1卖)',
                    'bi_idx': i,
                    'date': df['Date'].iloc[curr_bi.end_idx],
                    'price': curr_bi.end_price,
                    'force_ratio': ratio,
                })

        # 底背驰: 方向向下, 价格新低但力度衰竭
        elif curr_bi.direction == Direction.DOWN:
            if curr_bi.end_price < prev_bi.end_price and ratio < DIVERGENCE_RATIO:
                signals.append({
                    'type': '底背驰 (1买)',
                    'bi_idx': i,
                    'date': df['Date'].iloc[curr_bi.end_idx],
                    'price': curr_bi.end_price,
                    'force_ratio': ratio,
                })

    return signals

def identify_buy_sell_points(bis: List[Bi], zhongshus: List[Zhongshu],
                              df: pd.DataFrame, signals: List[dict]) -> List[dict]:
    """
    买卖点识别:
    一买: 底背驰确认点
    一卖: 顶背驰确认点
    二买: 回调至中枢低点(ZD)不破
    二卖: 反弹至中枢高点(ZG)不破
    三买: 中枢上方回踩不破ZG
    三卖: 中枢下方反抽不过ZD
    """
    points = []

    # 将背驰信号转为买卖点
    for sig in signals:
        if '底背驰' in sig['type']:
            points.append({'type': '一买(B1)', 'date': sig['date'],
                          'price': sig['price'], 'bi_idx': sig['bi_idx']})
        elif '顶背驰' in sig['type']:
            points.append({'type': '一卖(S1)', 'date': sig['date'],
                          'price': sig['price'], 'bi_idx': sig['bi_idx']})

    # 二买/三买: 分析笔与中枢的关系
    for zs in zhongshus:
        for j, bi in enumerate(bis):
            if bi.end_idx < zs.end_idx:
                continue
            # 向下笔末端, 如果触及中枢下沿但不跌破 → 二买
            if bi.direction == Direction.DOWN:
                bi_low = min(bi.start_price, bi.end_price)
                if zs.low * 0.97 <= bi_low <= zs.low * 1.03:
                    points.append({'type': '二买(B2)', 'date': df['Date'].iloc[bi.end_idx],
                                  'price': bi.end_price, 'bi_idx': j})
                elif bi_low > zs.high:
                    points.append({'type': '三买(B3)', 'date': df['Date'].iloc[bi.end_idx],
                                  'price': bi.end_price, 'bi_idx': j})
            # 向上笔末端
            elif bi.direction == Direction.UP:
                bi_high = max(bi.start_price, bi.end_price)
                if zs.high * 0.97 <= bi_high <= zs.high * 1.03:
                    points.append({'type': '二卖(S2)', 'date': df['Date'].iloc[bi.end_idx],
                                  'price': bi.end_price, 'bi_idx': j})
                elif bi_high < zs.low:
                    points.append({'type': '三卖(S3)', 'date': df['Date'].iloc[bi.end_idx],
                                  'price': bi.end_price, 'bi_idx': j})

    return points

# ═══════════════════════════════════════════════
# 8. 可视化
# ═══════════════════════════════════════════════
def plot_chanlun(df_raw: pd.DataFrame, df_std: pd.DataFrame, fractals: List[Fractal],
                 bis: List[Bi], zhongshus: List[Zhongshu], signals: List[dict],
                 points: List[dict]):
    """绘制缠论分析图"""
    plt.rcParams['font.sans-serif'] = ['Microsoft YaHei', 'SimHei', 'DejaVu Sans']
    plt.rcParams['axes.unicode_minus'] = False

    fig, axes = plt.subplots(3, 1, figsize=(22, 14),
                              gridspec_kw={'height_ratios': [4, 1, 1]},
                              sharex=True)
    ax1, ax2, ax3 = axes

    # ── 主图: K线 + 分型 + 笔 + 中枢 + 买卖点 ──
    dates = df_std['Date']
    closes = df_std['Close'].values

    # K线
    colors = ['#26a69a' if closes[i] >= df_std['Open'].iloc[i] else '#ef5350'
              for i in range(len(df_std))]
    width = max(0.4, 1.5 * 7 / len(df_std))
    ax1.bar(range(len(df_std)), df_std['High'] - df_std['Low'],
            bottom=df_std['Low'], color=colors, width=width, alpha=0.8)
    ax1.bar(range(len(df_std)), abs(closes - df_std['Open']),
            bottom=np.minimum(closes, df_std['Open']), color=colors, width=width * 1.8)

    # 分型标记
    for f in fractals:
        color = '#ef5350' if f.type == 'Top' else '#26a69a'
        marker = 'v' if f.type == 'Top' else '^'
        ax1.scatter(f.index, f.price, c=color, marker=marker, s=80, zorder=5,
                   edgecolors='white', linewidths=0.8)

    # 笔 (用不同颜色区分上下)
    for bi in bis:
        color = '#ef5350' if bi.direction == Direction.UP else '#26a69a'
        lw = 2.5
        ax1.plot([bi.start_idx, bi.end_idx], [bi.start_price, bi.end_price],
                color=color, linewidth=lw, alpha=0.7, zorder=3)

    # 中枢 (矩形阴影)
    for zs in zhongshus:
        rect = mpatches.Rectangle(
            (zs.start_idx - 0.3, zs.low), zs.end_idx - zs.start_idx + 0.6,
            zs.high - zs.low,
            facecolor='#7e57c2', alpha=0.2, edgecolor='#5e35b1',
            linewidth=1.5, linestyle='--', zorder=2
        )
        ax1.add_patch(rect)
        # 中枢标签
        mid_x = (zs.start_idx + zs.end_idx) / 2
        ax1.annotate(f'中枢\n${zs.low:.1f}-${zs.high:.1f}',
                    xy=(mid_x, zs.high), fontsize=7, color='#5e35b1',
                    ha='center', va='bottom', fontweight='bold')

    # 买卖点
    point_colors = {
        '一买(B1)': '#00c853', '二买(B2)': '#00e676', '三买(B3)': '#69f0ae',
        '一卖(S1)': '#d50000', '二卖(S2)': '#ff1744', '三卖(S3)': '#ff5252',
    }
    for p in points:
        idx = df_std[df_std['Date'] == p['date']].index
        if len(idx) == 0:
            continue
        idx = idx[0]
        color = point_colors.get(p['type'], 'black')
        ax1.scatter(idx, p['price'], c=color, marker='o', s=180, zorder=6,
                   edgecolors='white', linewidths=2)

    # 关键信号标注
    for sig in signals:
        idx = df_std[df_std['Date'] == sig['date']].index
        if len(idx) == 0:
            continue
        idx = idx[0]
        is_buy = '买' in sig['type']
        color = '#00c853' if is_buy else '#d50000'
        offset = 2.5 if is_buy else -2.5
        ax1.annotate(f"{sig['type']}\n${sig['price']:.1f}\n力度比={sig['force_ratio']:.2f}",
                    xy=(idx, sig['price']), xytext=(idx + 5, sig['price'] * (1 + offset/100)),
                    fontsize=7, color=color, fontweight='bold',
                    arrowprops=dict(arrowstyle='->', color=color, lw=1.2),
                    bbox=dict(boxstyle='round,pad=0.3', facecolor='white', alpha=0.85))

    # ── 副图1: MACD ──
    dif, dea, macd = calc_macd(df_std)
    x = range(len(df_std))
    ax2.plot(x, dif, color='#1a237e', linewidth=1.2, label='DIF')
    ax2.plot(x, dea, color='#b71c1c', linewidth=1.2, label='DEA')
    # MACD柱
    colors_macd = ['#26a69a' if v >= 0 else '#ef5350' for v in macd]
    ax2.bar(x, macd, color=colors_macd, width=width, alpha=0.7)
    ax2.axhline(y=0, color='gray', linestyle='-', linewidth=0.5)
    ax2.set_ylabel('MACD', fontsize=10)
    ax2.legend(loc='upper left', fontsize=8)
    ax2.grid(axis='y', alpha=0.3)

    # ── 副图2: 成交量 ──
    volumes = df_std['Volume'].values
    colors_vol = ['#26a69a' if closes[i] >= df_std['Open'].iloc[i] else '#ef5350'
                  for i in range(len(df_std))]
    ax3.bar(x, volumes, color=colors_vol, width=width, alpha=0.6)
    ax3.set_ylabel('Volume', fontsize=10)
    ax3.grid(axis='y', alpha=0.3)

    # ── 格式 ──
    # X轴标签
    tick_step = max(1, len(df_std) // 20)
    tick_idx = list(range(0, len(df_std), tick_step))
    tick_labels = [dates.iloc[i].strftime('%Y-%m') if i < len(dates) else '' for i in tick_idx]
    ax3.set_xticks(tick_idx)
    ax3.set_xticklabels(tick_labels, rotation=45, ha='right', fontsize=8)

    ax1.set_ylabel('Price ($/oz)', fontsize=11)
    ax1.set_title('COMEX 白银期货 周线 缠论分析 (Silver Weekly — Chan Theory)',
                  fontsize=14, fontweight='bold', pad=15)
    ax1.grid(axis='y', alpha=0.3)
    ax1.legend(handles=[
        mpatches.Patch(color='#ef5350', label='上升笔'),
        mpatches.Patch(color='#26a69a', label='下降笔'),
        mpatches.Patch(color='#7e57c2', alpha=0.3, label='中枢'),
        plt.Line2D([0], [0], marker='v', color='w', markerfacecolor='#ef5350',
                   markersize=10, label='顶分型'),
        plt.Line2D([0], [0], marker='^', color='w', markerfacecolor='#26a69a',
                   markersize=10, label='底分型'),
    ], loc='upper left', fontsize=8)

    plt.tight_layout()
    out_path = 'scripts/silver_weekly_chanlun.png'
    plt.savefig(out_path, dpi=150, bbox_inches='tight', facecolor='white')
    print(f"\n✅ 图表已保存: {out_path}")
    plt.close()

# ═══════════════════════════════════════════════
# 9. 主流程
# ═══════════════════════════════════════════════
def main():
    print("=" * 60)
    print("  白银周线缠论分析 (Chan Theory on Silver Weekly)")
    print("=" * 60)

    # 获取数据
    df_raw = fetch_silver_weekly()

    # Step 1: 包含处理
    df_std = process_inclusion(df_raw)
    print(f"\n📊 Step 1 - 包含处理: {len(df_raw)} → {len(df_std)} 根K线 ({len(df_raw) - len(df_std)} 根被合并)")

    # Step 2: 分型识别
    fractals = detect_fractals(df_std)
    tops = sum(1 for f in fractals if f.type == 'Top')
    bottoms = sum(1 for f in fractals if f.type == 'Bottom')
    print(f"📊 Step 2 - 分型识别: {len(fractals)} 个分型 (顶:{tops}, 底:{bottoms})")

    # Step 3: 笔识别
    bis = build_bis(fractals, df_std, min_bi_len=4)  # 周线用4略宽松
    up_bis = sum(1 for b in bis if b.direction == Direction.UP)
    down_bis = sum(1 for b in bis if b.direction == Direction.DOWN)
    print(f"📊 Step 3 - 笔识别: {len(bis)} 笔 (上升:{up_bis}, 下降:{down_bis})")

    # Step 4: 中枢识别
    zhongshus = build_zhongshus(bis)
    print(f"📊 Step 4 - 中枢识别: {len(zhongshus)} 个中枢")

    # Step 5: 背驰分析
    signals = analyze_divergence(bis, df_std)
    print(f"📊 Step 5 - 背驰分析: {len(signals)} 个背驰信号")

    # 买卖点识别
    points = identify_buy_sell_points(bis, zhongshus, df_std, signals)
    buy_pts = [p for p in points if '买' in p['type']]
    sell_pts = [p for p in points if '卖' in p['type']]
    print(f"📊 买卖点: {len(buy_pts)} 买点, {len(sell_pts)} 卖点")

    # ── 打印详细中枢 ──
    print("\n" + "─" * 50)
    print("中枢详情:")
    for i, zs in enumerate(zhongshus):
        start_date = df_std['Date'].iloc[zs.start_idx]
        end_date = df_std['Date'].iloc[zs.end_idx]
        print(f"  中枢{i+1}: {start_date.strftime('%Y-%m-%d')} ~ {end_date.strftime('%Y-%m-%d')}")
        print(f"         区间: ${zs.low:.2f} ~ ${zs.high:.2f} (宽度 ${zs.high - zs.low:.2f})")
        print(f"         包含 {len(zs.bis_in)} 笔")

    # ── 打印关键信号 ──
    if signals:
        print("\n" + "─" * 50)
        print("背驰/买卖信号:")
        for sig in signals:
            print(f"  {sig['type']} @ {sig['date'].strftime('%Y-%m-%d')} | "
                  f"价格=${sig['price']:.2f} | 力度比={sig['force_ratio']:.2f}")

    # ── 最新状态分析 ──
    print("\n" + "=" * 50)
    print("当前状态研判:")
    print("=" * 50)

    # 最后一笔方向
    if bis:
        last_bi = bis[-1]
        dir_str = "上升" if last_bi.direction == Direction.UP else "下降"
        bi_start_date = df_std['Date'].iloc[last_bi.start_idx]
        bi_end_date = df_std['Date'].iloc[last_bi.end_idx]
        print(f"  最后一笔: {dir_str}笔 ({bi_start_date.strftime('%Y-%m-%d')} → {bi_end_date.strftime('%Y-%m-%d')})")
        print(f"             ${last_bi.start_price:.2f} → ${last_bi.end_price:.2f} "
              f"({(last_bi.end_price/last_bi.start_price - 1)*100:+.1f}%)")

    # 当前价格相对中枢的位置
    if zhongshus:
        last_zs = zhongshus[-1]
        current_price = df_std['Close'].iloc[-1]
        if current_price > last_zs.high:
            print(f"  当前价格 ${current_price:.2f} 位于最近中枢上方 (中枢: ${last_zs.low:.1f}-${last_zs.high:.1f})")
            print(f"  → 中枢上方运行,关注三买机会 (若回踩不破ZG=${last_zs.high:.1f})")
        elif current_price < last_zs.low:
            print(f"  当前价格 ${current_price:.2f} 位于最近中枢下方 (中枢: ${last_zs.low:.1f}-${last_zs.high:.1f})")
            print(f"  → 中枢下方运行,关注三卖风险 (若反抽不过ZD=${last_zs.low:.1f})")
        else:
            print(f"  当前价格 ${current_price:.2f} 位于最近中枢内部 (中枢: ${last_zs.low:.1f}-${last_zs.high:.1f})")
            print(f"  → 中枢震荡中,等待方向选择")

    # 最近的买卖点
    if points:
        recent = sorted(points, key=lambda p: p['date'], reverse=True)
        print(f"\n  最近信号: {recent[0]['type']} @ {recent[0]['date'].strftime('%Y-%m-%d')} ${recent[0]['price']:.2f}")

    # 背驰状态
    if signals:
        last_signal = signals[-1]
        print(f"  最近背驰: {last_signal['type']} @ {last_signal['date'].strftime('%Y-%m-%d')} (力度比={last_signal['force_ratio']:.2f})")

    # ── 生成图表 ──
    plot_chanlun(df_raw, df_std, fractals, bis, zhongshus, signals, points)

    print("\n✅ 缠论分析完成")

if __name__ == '__main__':
    main()
