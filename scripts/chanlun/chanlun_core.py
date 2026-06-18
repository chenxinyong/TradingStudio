"""
缠论核心算法 — Python 参考实现 (P0 原型)
===========================================
严格按照 docs/strategies/chanlun/04-quant-spec.md 实现。

模块结构:
  - chanlun_core.py (本文件): 包含处理 → 分型 → 笔 → 中枢 完整链路
  - 对应 C# 目标命名空间: TradingStudio.Strategy.ChanLun

每个函数/类都标注了对应的 quant-spec 章节号，方便交叉验证。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
from decimal import Decimal
from enum import Enum
from typing import List, Optional, Tuple, Union

# ── 常量 (spec §0.2) ──────────────────────────────────────────

MIN_BI_LEN = 7          # 最小笔长度（包含K线数）
MAX_BI_NUM = 50         # 最大笔数量
ZS_MIN_OVERLAP = 1      # 中枢最小重叠（价格单位）


# ── 基础类型 ──────────────────────────────────────────────────

class Direction(Enum):
    """方向 — spec §1.2"""
    Up = "UP"
    Down = "DOWN"


class FractalType(Enum):
    """分型类型 — spec §2.1"""
    Top = "TOP"       # 顶分型
    Bottom = "BOTTOM" # 底分型


@dataclass
class Bar:
    """K线 — spec §0.1"""
    dt: datetime
    open: float
    high: float
    low: float
    close: float
    vol: int = 0


@dataclass
class Fractal:
    """分型 — spec §2.1"""
    type: FractalType
    index: int          # 在标准K线数组中的位置
    price: float        # Top=K线.High, Bottom=K线.Low
    dt: datetime


@dataclass
class Bi:
    """笔 — spec §3.1"""
    type: Direction     # Up / Down
    start_fx: Fractal
    end_fx: Fractal
    start_idx: int
    end_idx: int
    bar_count: int      # 包含的标准K线数
    low: float
    high: float
    dt_start: datetime
    dt_end: datetime

    # 力度指标 — spec §3.3
    @property
    def power(self) -> float:
        """涨跌幅度（万分比）"""
        if self.type == Direction.Up:
            return (self.high - self.low) / self.low * 10000
        else:
            return (self.high - self.low) / self.high * 10000

    @property
    def change_pct(self) -> float:
        """涨跌幅（百分比）"""
        if self.type == Direction.Up:
            return (self.end_fx.price - self.start_fx.price) / self.start_fx.price * 100
        else:
            return (self.start_fx.price - self.end_fx.price) / self.start_fx.price * 100


@dataclass
class Zhongshu:
    """中枢 — spec §4.1"""
    zg: float          # 中枢上沿 = min(三笔高点中的最大值)
    zd: float          # 中枢下沿 = max(三笔低点中的最小值)
    zz: float          # 中枢中轨 = (zg+zd)/2
    start_bi_idx: int  # 第一笔在bi_list中的索引
    end_bi_idx: int    # 第三笔在bi_list中的索引
    bi_count: int      # 构成笔数（默认3，延伸后更大）
    level: int = 1     # 中枢级别（1=笔中枢）
    dt_start: Optional[datetime] = None
    dt_end: Optional[datetime] = None


# ── §1: 包含处理 ──────────────────────────────────────────────

def has_inclusion(a: Bar, b: Bar) -> bool:
    """检查两根K线是否存在包含关系 — spec §1.1"""
    return (a.high >= b.high and a.low <= b.low) or \
           (b.high >= a.high and b.low <= a.low)


def process_inclusions(raw_bars: List[Bar]) -> List[Bar]:
    """
    包含处理 — spec §1.2-§1.4

    将所有具有包含关系的相邻K线合并为标准K线。
    合并方向由前两根非包含K线的关系决定。
    """
    if len(raw_bars) < 2:
        return list(raw_bars)

    direction: Optional[Direction] = None
    result: List[Bar] = []
    pending: Optional[Bar] = None

    for bar in raw_bars:
        if pending is None:
            pending = bar
            continue

        if not has_inclusion(pending, bar):
            # 无包含 → pending 确认，bar 变新的 pending
            if len(result) >= 1:
                prev = result[-1]
                if bar.high > prev.high:
                    direction = Direction.Up
                elif bar.high < prev.high:
                    direction = Direction.Down
                # 相等时保持原方向
            result.append(pending)
            pending = bar
        else:
            # 有包含 → 合并到 pending
            if len(result) >= 1:
                prev = result[-1]
                if pending.high > prev.high:
                    direction = Direction.Up
                elif pending.high < prev.high:
                    direction = Direction.Down
            d = direction or Direction.Up

            if d == Direction.Up:
                pending = Bar(
                    dt=bar.dt,
                    open=pending.open,
                    close=bar.close,
                    high=max(pending.high, bar.high),
                    low=max(pending.low, bar.low),
                    vol=pending.vol + bar.vol,
                )
            else:
                pending = Bar(
                    dt=bar.dt,
                    open=pending.open,
                    close=bar.close,
                    high=min(pending.high, bar.high),
                    low=min(pending.low, bar.low),
                    vol=pending.vol + bar.vol,
                )

    if pending is not None:
        result.append(pending)
    return result


def validate_inclusions(original: List[Bar], processed: List[Bar]) -> bool:
    """验证包含处理结果 — spec §1.5"""
    # Assertion 1: result.Count <= rawBars.Count
    if len(processed) > len(original):
        return False
    # Assertion 2: 任意相邻两根标准K线无包含
    for i in range(len(processed) - 1):
        if has_inclusion(processed[i], processed[i + 1]):
            return False
    # Assertion 3: 时间单调递增
    for i in range(len(processed) - 1):
        if processed[i].dt >= processed[i + 1].dt:
            return False
    return True


# ── §2: 分型识别 ──────────────────────────────────────────────

def find_fractals(std_bars: List[Bar]) -> List[Fractal]:
    """
    识别顶底分型 — spec §2.2

    条件:
      顶分型: b.high > a.high && b.high > c.high && b.low > a.low && b.low > c.low
      底分型: b.low  < a.low  && b.low  < c.low  && b.high < a.high && b.high < c.high
    """
    if len(std_bars) < 3:
        return []

    fractals: List[Fractal] = []
    for i in range(1, len(std_bars) - 1):
        a, b, c = std_bars[i - 1], std_bars[i], std_bars[i + 1]

        # 顶分型
        if (b.high > a.high and b.high > c.high and
                b.low > a.low and b.low > c.low):
            fractals.append(Fractal(FractalType.Top, i, b.high, b.dt))

        # 底分型
        elif (b.low < a.low and b.low < c.low and
                b.high < a.high and b.high < c.high):
            fractals.append(Fractal(FractalType.Bottom, i, b.low, b.dt))

    return fractals


def deduplicate_fractals(fractals: List[Fractal]) -> List[Fractal]:
    """
    分型去重 — spec §2.3

    相邻同类型分型 → 保留极值更极端者:
      - 连续两个 TOP → 保留 Price 更高的
      - 连续两个 BOTTOM → 保留 Price 更低的
    """
    if len(fractals) < 2:
        return fractals

    result = [fractals[0]]
    for f in fractals[1:]:
        last = result[-1]
        if f.type == last.type:
            if f.type == FractalType.Top and f.price > last.price:
                result[-1] = f
            elif f.type == FractalType.Bottom and f.price < last.price:
                result[-1] = f
        else:
            result.append(f)
    return result


def validate_fractals(fractals: List[Fractal], std_bars: List[Bar]) -> bool:
    """验证分型结果 — spec §2.4"""
    if len(fractals) < 2:
        return True
    # Assertion 1: Top 和 Bottom 严格交替
    for i in range(len(fractals) - 1):
        if fractals[i].type == fractals[i + 1].type:
            return False
    # Assertion 2 & 3: price 正确
    for f in fractals:
        bar = std_bars[f.index]
        if f.type == FractalType.Top and abs(float(f.price) - float(bar.high)) > 1e-9:
            return False
        if f.type == FractalType.Bottom and abs(float(f.price) - float(bar.low)) > 1e-9:
            return False
    return True


# ── §3: 笔 ───────────────────────────────────────────────────

def build_bis(fractals: List[Fractal], std_bars: List[Bar],
              min_bi_len: int = MIN_BI_LEN) -> List[Bi]:
    """
    从分型构建笔 — spec §3.2 (改进版：允许非相邻分型连接)

    算法:
      - 从最左分型开始，向右扫描寻找第一个有效的终点分型
      - 有效条件: 不同类型 + 足够K线数 + 方向正确
      - 找到 → 记录笔，从终点继续
      - 未找到 → 跳过当前起点，从下一个分型继续

    笔成立条件:
      1. start_fx 和 end_fx 类型不同
      2. end_idx - start_idx + 1 >= MIN_BI_LEN
      3. Up笔: end_fx.Price > start_fx.Price
         Down笔: start_fx.Price > end_fx.Price
      4. 笔内所有K线的极值遵循笔方向 (Up: 最高点在终点, Down: 最低点在终点)
    """
    if len(fractals) < 2:
        return []

    bis: List[Bi] = []
    i = 0
    while i < len(fractals) - 1:
        start = fractals[i]
        found = False

        # 向右扫描，寻找第一个有效终点
        for j in range(i + 1, min(i + 1 + MAX_BI_NUM, len(fractals))):
            end = fractals[j]

            # 1) 类型必须不同
            if start.type == end.type:
                continue

            # 2) K线数检查
            kline_count = end.index - start.index + 1
            if kline_count < min_bi_len:
                continue

            # 3) 方向 + 价格单调性检查
            if start.type == FractalType.Bottom and end.type == FractalType.Top:
                bi_type = Direction.Up
                if end.price <= start.price:
                    continue
            else:
                bi_type = Direction.Down
                if start.price <= end.price:
                    continue

            # 计算笔的极值区间
            seg_bars = std_bars[start.index:end.index + 1]
            bi_high = max(b.high for b in seg_bars)
            bi_low = min(b.low for b in seg_bars)

            bis.append(Bi(
                type=bi_type,
                start_fx=start,
                end_fx=end,
                start_idx=start.index,
                end_idx=end.index,
                bar_count=kline_count,
                low=bi_low,
                high=bi_high,
                dt_start=start.dt,
                dt_end=end.dt,
            ))
            i = j  # 跳到终点分型继续
            found = True
            break

        if not found:
            i += 1  # 无有效终点，跳过当前分型

    # 后处理: 合并相邻同向笔
    bis = _merge_same_direction_bis(bis, std_bars)

    return bis


def _merge_same_direction_bis(bis: List[Bi], std_bars: List[Bar]) -> List[Bi]:
    """合并相邻同向笔（跳过中间分型可能导致同向相邻笔）"""
    if len(bis) < 2:
        return bis

    merged = [bis[0]]
    for bi in bis[1:]:
        last = merged[-1]
        if bi.type == last.type:
            # 合并: 取更宽的范围
            seg = std_bars[last.start_idx:bi.end_idx + 1]
            merged[-1] = Bi(
                type=last.type,
                start_fx=last.start_fx,
                end_fx=bi.end_fx,
                start_idx=last.start_idx,
                end_idx=bi.end_idx,
                bar_count=bi.end_idx - last.start_idx + 1,
                low=min(b.low for b in seg),
                high=max(b.high for b in seg),
                dt_start=last.dt_start,
                dt_end=bi.dt_end,
            )
        else:
            merged.append(bi)
    return merged


def validate_bis(bis: List[Bi], min_bi_len: int = MIN_BI_LEN) -> bool:
    """验证笔结果 — spec §3.4"""
    if len(bis) < 2:
        return True
    for i in range(len(bis)):
        bi = bis[i]
        # 1. 相邻笔方向相反
        if i > 0 and bis[i].type == bis[i - 1].type:
            return False
        # 2. BarCount >= MinBiLen
        if bi.bar_count < min_bi_len:
            return False
        # 3. 价格约束
        if bi.type == Direction.Up:
            if not (bi.high >= bi.low and bi.end_fx.price > bi.start_fx.price):
                return False
        else:
            if not (bi.high >= bi.low and bi.start_fx.price > bi.end_fx.price):
                return False
    return True


# ── §4: 中枢 ──────────────────────────────────────────────────

def build_zhongshus(bis: List[Bi], min_overlap: float = ZS_MIN_OVERLAP) -> List[Zhongshu]:
    """
    从笔构建中枢 — spec §4.2

    连续三笔 bi[i], bi[i+1], bi[i+2]:
      ZG = min(bi[i].high, bi[i+1].high, bi[i+2].high)
      ZD = max(bi[i].low,  bi[i+1].low,  bi[i+2].low)
      if ZG > ZD + ZS_MIN_OVERLAP → 中枢成立

    相邻重叠的中枢自动合并。
    """
    if len(bis) < 3:
        return []

    zs_list: List[Zhongshu] = []
    i = 0
    while i < len(bis) - 2:
        b1, b2, b3 = bis[i], bis[i + 1], bis[i + 2]
        zg = min(b1.high, b2.high, b3.high)
        zd = max(b1.low, b2.low, b3.low)

        if zg > zd + min_overlap:
            zs = Zhongshu(
                zg=zg, zd=zd, zz=(zg + zd) / 2,
                start_bi_idx=i, end_bi_idx=i + 2,
                bi_count=3, level=1,
                dt_start=b1.dt_start, dt_end=b3.dt_end,
            )

            # 重叠合并
            if zs_list and _zs_overlap(zs_list[-1], zs):
                zs_list[-1] = _merge_zhongshus(zs_list[-1], zs)
            else:
                zs_list.append(zs)
            i += 1
        else:
            i += 1

    return zs_list


def _zs_overlap(a: Zhongshu, b: Zhongshu) -> bool:
    """检查两个中枢是否有重叠 — spec §4.2"""
    return a.zg > b.zd and b.zg > a.zd


def _merge_zhongshus(a: Zhongshu, b: Zhongshu) -> Zhongshu:
    """合并两个重叠中枢 — spec §4.2"""
    return Zhongshu(
        zg=max(a.zg, b.zg),
        zd=min(a.zd, b.zd),
        zz=(max(a.zg, b.zg) + min(a.zd, b.zd)) / 2,
        start_bi_idx=a.start_bi_idx,
        end_bi_idx=b.end_bi_idx,
        bi_count=a.bi_count + b.bi_count - 2,
        level=a.level,
        dt_start=a.dt_start,
        dt_end=b.dt_end,
    )


# ── §4.3: 走势分类 ────────────────────────────────────────────

def classify_trend(zs_list: List[Zhongshu]) -> str:
    """
    走势分类 — spec §4.3

    返回: "UP" | "DOWN" | "CONSOLIDATION" | "UNCLASSIFIED"
    """
    if len(zs_list) == 0:
        return "UNCLASSIFIED"
    if len(zs_list) == 1:
        return "CONSOLIDATION"

    direction = None
    for i in range(len(zs_list) - 1):
        zs1, zs2 = zs_list[i], zs_list[i + 1]
        if zs2.zd > zs1.zg:  # 中枢上移
            if direction == "DOWN":
                return "CONSOLIDATION"
            direction = "UP"
        elif zs2.zg < zs1.zd:  # 中枢下移
            if direction == "UP":
                return "CONSOLIDATION"
            direction = "DOWN"
        else:
            return "CONSOLIDATION"
    return direction or "CONSOLIDATION"


# ── 完整管线 ──────────────────────────────────────────────────

@dataclass
class ChanLunResult:
    """缠论分析完整结果"""
    raw_bars: List[Bar] = field(default_factory=list)
    std_bars: List[Bar] = field(default_factory=list)  # 无包含标准K线
    fractals: List[Fractal] = field(default_factory=list)
    bis: List[Bi] = field(default_factory=list)
    zhongshus: List[Zhongshu] = field(default_factory=list)
    trend: str = "UNCLASSIFIED"


def analyze(raw_bars: List[Bar],
            min_bi_len: int = MIN_BI_LEN,
            min_zs_overlap: float = ZS_MIN_OVERLAP) -> ChanLunResult:
    """
    完整缠论分析管线 — 一键运行全流程。

    输入: 原始K线列表（按时间升序）
    输出: ChanLunResult（包含处理/分型/笔/中枢/走势分类）
    """
    # Step 1: 包含处理
    std_bars = process_inclusions(raw_bars)

    # Step 2: 分型识别
    raw_fractals = find_fractals(std_bars)
    fractals = deduplicate_fractals(raw_fractals)

    # Step 3: 笔识别
    bis = build_bis(fractals, std_bars, min_bi_len=min_bi_len)

    # Step 4: 中枢识别
    zhongshus = build_zhongshus(bis, min_overlap=min_zs_overlap)

    # Step 5: 走势分类
    trend = classify_trend(zhongshus)

    return ChanLunResult(
        raw_bars=raw_bars,
        std_bars=std_bars,
        fractals=fractals,
        bis=bis,
        zhongshus=zhongshus,
        trend=trend,
    )
