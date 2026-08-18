#!/usr/bin/env python3
"""
日内 5 分钟动量策略：30 分钟定方向，日内平仓
===============================================
基于 Ernie Chan 日内动量（Intraday Momentum）研究发现：

核心假设（Chan 实证）：
  开盘前 30 分钟的走势方向，对"剩余时间→收盘"有显著的延续预测能力。
  这不是开盘跳空后的均值回归，而是趋势在日内的自我强化。

策略全流程：
  ┌───────────── 观察期 09:00-09:30 ─────────────┐
  │ 跟踪 6 根 5 分钟 K 线                           │
  │ 分析：方向 / 动能 / 连续性 / 量价                │
  │ → 综合评分 → 决定方向                           │
  └──────────────────────────────────────────────┘
                      ↓ 09:30 入场
  ┌───────────── 持仓期 09:30-15:00 ─────────────┐
  │ 初始止损 0.5%                                   │
  │ 浮盈 0.5% → 止损移至保本                        │
  │ 浮盈 1.0% → 启动移动止盈（回撤 0.3% 离场）       │
  │ 11:30 上午收盘检查（浮亏则考虑提前离场）           │
  │ 14:55 强制平仓（不隔夜）                         │
  └──────────────────────────────────────────────┘

与日线 TSMOM 组合确认（Chan 推荐）：
  日线动量 ↑ + 日内 30min ↑ → 做多（高确信度）
  日线动量 ↓ + 日内 30min ↓ → 做空（高确信度）
  方向不一致 → 不做（过滤假突破）
"""

import urllib.request
import re
import json
import time
import os
import warnings
from datetime import datetime
from dataclasses import dataclass, field
import numpy as np
import pandas as pd

warnings.filterwarnings('ignore')

# ============================================================================
# 配置
# ============================================================================

@dataclass
class IntradayConfig:
    """日内策略参数 —— 集中管理，便于调参"""

    # ── 5 分钟 K 线参数 ──
    observation_bars: int = 6          # 观察期 K 线条数（6 × 5min = 30min）
    bar_interval_min: int = 5         # K 线周期（分钟）
    day_session_start: str = '09:00'  # 日盘开盘时间
    signal_time: str = '09:30'        # 信号确认时间
    force_exit_time: str = '14:55'    # 强制平仓时间
    midday_check_time: str = '11:30'  # 上午收盘检查

    # ── 入场条件（综合评分制）──
    min_30min_return: float = 0.0015  # 30 分钟净涨跌幅阈值 (0.15%)
    min_bar_continuity: float = 0.60  # 方向一致 K 线占比（≥3.6/6，放宽）
    require_acceleration: bool = False # 是否要求动能加速（默认关——模拟数据不可靠）
    max_adverse_pct: float = 0.005    # 30 分钟内的最大不利偏移 (0.5%)

    # ── 日线 TSMOM 过滤器 ──
    use_daily_filter: bool = True     # 是否启用日线动量过滤
    tsmom_lookback_days: int = 252    # 日线 TSMOM 回看天数

    # ── 风控 ──
    hard_stop_pct: float = 0.005      # 硬止损 0.5%
    breakeven_trigger: float = 0.005  # 浮盈达 0.5% → 止损移至保本
    trail_trigger: float = 0.010      # 浮盈达 1.0% → 启动移动止盈
    trail_distance: float = 0.003     # 移动止盈回撤距离 0.3%
    max_daily_loss_pct: float = 0.02  # 单日最大亏损 2%
    max_cons_losses: int = 3          # 连续亏损后当日停止

    # ── 仓位 ──
    risk_per_trade_pct: float = 0.005 # 每笔风险 0.5% 总资金
    max_position_pct: float = 0.25    # 单品种保证金上限 25%

    # ── 成本 ──
    commission_per_lot: float = 8.0   # 每手手续费（双边，元）
    slippage_ticks: int = 1           # 滑点（跳）

    # ── 回测 ──
    start_date: str = '2022-01-01'
    end_date: str = '2026-07-25'
    initial_capital: float = 1_000_000
    n_simulations_per_day: int = 30   # 每天模拟路径数（降噪）


# 合约规格（用于仓位计算）
CONTRACT_SPECS = {
    'RB': {'multiplier': 10,  'tick': 1.0,  'margin': 0.10, 'name': '螺纹钢'},
    'JM': {'multiplier': 60,  'tick': 0.5,  'margin': 0.12, 'name': '焦煤'},
    'CU': {'multiplier': 5,   'tick': 10.0, 'margin': 0.10, 'name': '沪铜'},
    'AU': {'multiplier': 1000,'tick': 0.02, 'margin': 0.08, 'name': '沪金'},
    'SC': {'multiplier': 1000,'tick': 0.1,  'margin': 0.12, 'name': '原油'},
    'I':  {'multiplier': 100, 'tick': 0.5,  'margin': 0.12, 'name': '铁矿石'},
    'M':  {'multiplier': 10,  'tick': 1.0,  'margin': 0.08, 'name': '豆粕'},
    'MA': {'multiplier': 10,  'tick': 1.0,  'margin': 0.08, 'name': '甲醇'},
}


# ============================================================================
# 第一部分：5 分钟 K 线数据
# ============================================================================

def fetch_5min_bars(symbol_base):
    """
    从新浪 API 获取当日 5 分钟 K 线。

    已验证：新浪期货 API 的 getMinLine 方法返回当前交易时段的分钟数据。
    使用 type=5 理论上指定 5 分钟间隔（实测返回原始间隔，需自行聚合）。

    返回: pd.DataFrame | None
    """
    sym = f"{symbol_base}0"
    url = (
        f"https://stock2.finance.sina.com.cn/futures/api/jsonp.php/"
        f"var%20m5_{sym}=/InnerFuturesNewService.getMinLine?"
        f"symbol={sym}&type=5"
    )
    try:
        req = urllib.request.Request(url)
        req.add_header("User-Agent", "Mozilla/5.0")
        data = urllib.request.urlopen(req, timeout=10).read().decode()
        m = re.search(r'\((\[.*\])\)', data, re.DOTALL)
        if not m:
            return None
        records = json.loads(m.group(1))
        return _parse_min_records(records)
    except Exception as e:
        print(f"  [ERR] fetch {sym}: {e}")
    return None


def _parse_min_records(records):
    """解析新浪分钟数据 → DataFrame（含 open/open_interest）"""
    rows, cur_date = [], None
    for r in records:
        if len(r) >= 7:
            t, o, c, v, oi, _, ds = r[0], float(r[1]), float(r[2]), float(r[3]), float(r[4]), r[5], r[6]
            cur_date = ds
        elif len(r) >= 5:
            t, o, c, v, oi = r[0], float(r[1]), float(r[2]), float(r[3]), float(r[4])
        else:
            continue
        if cur_date:
            rows.append({'dt': pd.Timestamp(f"{cur_date} {t}"),
                         'open': o, 'close': c, 'volume': v, 'oi': oi})
    df = pd.DataFrame(rows)
    return df.set_index('dt').sort_index() if not df.empty else pd.DataFrame()


# ============================================================================
# 第二部分：K 线形态分析引擎
# ============================================================================

@dataclass
class BarPattern:
    """单根 5 分钟 K 线的形态特征"""
    open: float
    high: float
    low: float
    close: float
    volume: float = 0.0
    body: float = 0.0        # 实体 (close - open)
    upper_wick: float = 0.0  # 上影线
    lower_wick: float = 0.0  # 下影线
    direction: int = 0       # +1 阳线, -1 阴线, 0 十字星
    body_pct: float = 0.0    # 实体占振幅的百分比

    @classmethod
    def from_ohlc(cls, o, h, l, c, v=0):
        body = c - o
        upper = h - max(o, c)
        lower = min(o, c) - l
        total_range = h - l
        body_pct = abs(body) / total_range if total_range > 0 else 0
        direction = 1 if body > 0 else (-1 if body < 0 else 0)
        return cls(o, h, l, c, v, body, upper, lower, direction, body_pct)


@dataclass
class ObservationSummary:
    """30 分钟观察期（6 根 5 分钟 K 线）的综合分析结果"""
    # 基本统计
    open_price: float = 0.0
    close_price: float = 0.0
    ret_30min: float = 0.0       # 30分钟涨跌幅
    high_30min: float = 0.0
    low_30min: float = 0.0
    range_30min: float = 0.0     # 30 分钟振幅

    # K 线形态
    bars: list = field(default_factory=list)
    n_bullish: int = 0           # 阳线数量
    n_bearish: int = 0           # 阴线数量
    continuity: float = 0.0      # 方向一致性（主导方向占比）
    dominant_dir: int = 0        # 主导方向

    # 动能分析
    first_3_range: float = 0.0   # 前 15 分钟振幅
    last_3_range: float = 0.0    # 后 15 分钟振幅
    acceleration: float = 0.0    # 动能加速比 (>1 = 加速)
    bar_body_avg: float = 0.0    # 平均实体占比
    max_adverse: float = 0.0     # 30 分钟内最大不利偏移

    # 综合评分 (0-100)
    score: float = 0.0


def analyze_observation_window(bars_5min):
    """
    分析 30 分钟观察期的 K 线形态，输出综合研判。

    这是策略的核心 —— 将 Chan 的"30 分钟涨跌"扩展为
    多维度的 K 线形态分析。

    参数:
        bars_5min: list[dict], 每根 K 线含 open/high/low/close/volume
    返回:
        ObservationSummary: 完整分析结果
    """
    if len(bars_5min) < 4:
        return ObservationSummary()

    obs = ObservationSummary()
    obs.bars = [BarPattern.from_ohlc(b['open'], b['high'], b['low'],
                                      b['close'], b.get('volume', 0))
                for b in bars_5min]

    # ── 基本统计 ──
    obs.open_price = bars_5min[0]['open']
    obs.close_price = bars_5min[-1]['close']
    obs.ret_30min = obs.close_price / obs.open_price - 1
    obs.high_30min = max(b['high'] for b in bars_5min)
    obs.low_30min = min(b['low'] for b in bars_5min)
    obs.range_30min = obs.high_30min / obs.low_30min - 1

    # ── 方向一致性 ──
    obs.n_bullish = sum(1 for b in obs.bars if b.direction > 0)
    obs.n_bearish = sum(1 for b in obs.bars if b.direction < 0)
    n = len(obs.bars)
    obs.dominant_dir = 1 if obs.n_bullish >= obs.n_bearish else -1
    obs.continuity = max(obs.n_bullish, obs.n_bearish) / n

    # ── 动能加速分析 ──
    # 比较前 15 分钟 vs 后 15 分钟的振幅
    # 如果后段振幅更大 → 动能在加速（趋势在强化）
    mid = n // 2
    obs.first_3_range = (max(b['high'] for b in bars_5min[:mid]) /
                         min(b['low'] for b in bars_5min[:mid]) - 1)
    obs.last_3_range = (max(b['high'] for b in bars_5min[mid:]) /
                        min(b['low'] for b in bars_5min[mid:]) - 1)
    obs.acceleration = (obs.last_3_range / obs.first_3_range
                        if obs.first_3_range > 0 else 1.0)

    # ── K 线质量 ──
    obs.bar_body_avg = np.mean([abs(b.body_pct) for b in obs.bars])

    # ── 最大不利偏移 ──
    # 如果做多，30 分钟内从最高点回撤多少
    cum_max = obs.open_price
    cum_min = obs.open_price
    max_adverse_long = 0.0
    max_adverse_short = 0.0
    for b in bars_5min:
        cum_max = max(cum_max, b['high'])
        cum_min = min(cum_min, b['low'])
        max_adverse_long = max(max_adverse_long, (cum_max - b['low']) / obs.open_price)
        max_adverse_short = max(max_adverse_short, (b['high'] - cum_min) / obs.open_price)
    obs.max_adverse = max(max_adverse_long, max_adverse_short)

    return obs


# ============================================================================
# 第三部分：入场决策引擎
# ============================================================================

def evaluate_entry_signal(obs: ObservationSummary, daily_dir: int,
                          config: IntradayConfig) -> dict:
    """
    综合评分制入场决策。

    Chan 的原始逻辑是简单的 sign(30min return)，这里扩展为多维评分：
      - 方向分 (40%)：30 分钟净涨跌
      - 一致性分 (25%)：阳/阴线占比
      - 动能分 (20%)：后段加速 vs 前段
      - 质量分 (15%)：最大不利偏移小、实体占比大

    返回:
        dict: {signal, score, reason, ...}
    """
    result = {
        'signal': 0,          # +1=做多, -1=做空, 0=不交易
        'score': 0.0,         # 0-100
        'confidence': 'none', # high / medium / low / none
        'reject_reasons': [], # 被拒绝的原因
        'long_score': 0.0,
        'short_score': 0.0,
    }

    if obs.ret_30min == 0:
        result['reject_reasons'].append('flat_30min')
        return result

    direction = np.sign(obs.ret_30min)

    # ── 日线 TSMOM 方向过滤 ──
    if config.use_daily_filter and daily_dir != 0:
        if direction != daily_dir:
            result['reject_reasons'].append(
                f'方向冲突(日内={direction}, 日线={daily_dir})'
            )
            return result

    # ── 条件 1: 30 分钟净涨跌幅必须超过阈值 ──
    if abs(obs.ret_30min) < config.min_30min_return:
        result['reject_reasons'].append(
            f'涨跌幅不足({obs.ret_30min:.4f} < {config.min_30min_return})'
        )
        return result

    # ── 条件 2: K 线方向一致性 ──
    if obs.continuity < config.min_bar_continuity:
        result['reject_reasons'].append(
            f'方向一致性不足({obs.continuity:.0%} < {config.min_bar_continuity:.0%})'
        )
        return result

    # ── 条件 3: 最大不利偏移 ──
    if obs.max_adverse > config.max_adverse_pct:
        result['reject_reasons'].append(
            f'30min内回撤过大({obs.max_adverse:.4f} > {config.max_adverse_pct})'
        )
        return result

    # ── 条件 4: 动能加速（可选）──
    if config.require_acceleration and obs.acceleration < 0.8:
        result['reject_reasons'].append(
            f'动能衰减(accel={obs.acceleration:.2f} < 0.8)'
        )
        return result

    # ── 综合评分 ──
    # 方向分（40%）：收益率标准化到 [-1, 1] 映射到 [0, 40]
    dir_score = min(40, max(0, abs(obs.ret_30min) / 0.005 * 20 + 20))

    # 一致性分（25%）：连续性 0.67-1.0 映射到 [0, 25]
    cons_score = min(25, (obs.continuity - 0.5) / 0.5 * 25)

    # 动能分（20%）：加速比 0.8-2.0 映射到 [0, 20]
    accel_score = min(20, max(0, (obs.acceleration - 0.8) / 1.2 * 20))

    # 质量分（15%）：回撤小+实体占比大
    quality = max(0, (1 - obs.max_adverse / config.max_adverse_pct)) * 7.5
    quality += min(7.5, obs.bar_body_avg * 15)
    quality_score = min(15, quality)

    total_score = dir_score + cons_score + accel_score + quality_score

    # 确定置信度
    if total_score >= 70:
        confidence = 'high'
    elif total_score >= 50:
        confidence = 'medium'
    else:
        confidence = 'low'

    result['signal'] = direction
    result['score'] = total_score
    result['confidence'] = confidence
    result['long_score'] = total_score if direction > 0 else 0
    result['short_score'] = total_score if direction < 0 else 0

    return result


# ============================================================================
# 第四部分：持仓管理器
# ============================================================================

class PositionManager:
    """
    日内持仓管理。

    四阶段离场逻辑（优先级从高到低）：
      1. 硬止损触发 → 立即市价离场
      2. 强制平仓时间 (14:55) → 市价离场
      3. 移动止盈触发 → 市价离场
      4. 上午收盘浮亏 → 考虑离场
    """

    def __init__(self, config, entry_price, direction, contract_spec):
        self.config = config
        self.entry_price = entry_price
        self.direction = direction          # +1 or -1
        self.spec = contract_spec

        # 止损价格
        self.stop_price = entry_price * (1 - direction * config.hard_stop_pct)
        self.breakeven_price = entry_price   # 保本止损位
        self.trail_high = entry_price        # 移动止盈参考高点（多）
        self.trail_low = entry_price         # 移动止盈参考低点（空）
        self.trail_active = False

        # 状态
        self.exit_price = None
        self.exit_reason = None
        self.max_favorable = 0.0             # 最大浮盈（%）
        self.max_adverse = 0.0               # 最大浮亏（%）

    def update(self, current_price, current_time_str):
        """
        用当前价格更新持仓状态，返回是否需要离场。

        参数:
            current_price: 当前价格
            current_time_str: 当前时间字符串 'HH:MM'
        返回:
            tuple: (should_exit: bool, exit_price: float, exit_reason: str)
        """
        # 计算浮盈
        if self.direction == 1:
            pnl_pct = (current_price / self.entry_price - 1)
        else:
            pnl_pct = (self.entry_price / current_price - 1)

        self.max_favorable = max(self.max_favorable, pnl_pct)
        self.max_adverse = max(self.max_adverse, -pnl_pct)

        # ── 优先级 1: 硬止损 ──
        if self.direction == 1 and current_price <= self.stop_price:
            return (True, self.stop_price, 'hard_stop')
        elif self.direction == -1 and current_price >= self.stop_price:
            return (True, self.stop_price, 'hard_stop')

        # ── 优先级 2: 强制平仓时间 ──
        if current_time_str >= self.config.force_exit_time:
            return (True, current_price, 'force_exit')

        # ── 优先级 3: 移动止盈 ──
        if pnl_pct >= self.config.breakeven_trigger:
            # 保本
            self.stop_price = self.entry_price

        if pnl_pct >= self.config.trail_trigger:
            self.trail_active = True
            if self.direction == 1:
                self.trail_high = max(self.trail_high, current_price)
                trail_stop = self.trail_high * (1 - self.config.trail_distance)
                if current_price <= trail_stop:
                    return (True, trail_stop, 'trailing_stop')
            else:
                self.trail_low = min(self.trail_low, current_price)
                trail_stop = self.trail_low * (1 + self.config.trail_distance)
                if current_price >= trail_stop:
                    return (True, trail_stop, 'trailing_stop')

        # ── 优先级 4: 上午收盘浮亏检查 ──
        if current_time_str >= self.config.midday_check_time:
            if pnl_pct < -0.001:  # 浮亏超过 0.1%
                return (True, current_price, 'midday_cut')

        return (False, None, None)

    def get_max_adverse_pct(self):
        return self.max_adverse


# ============================================================================
# 第五部分：日内路径模拟与回测引擎
# ============================================================================

def simulate_5min_bars(o, h, l, c, n_bars=48, seed=None):
    """
    生成一天完整的 5 分钟 K 线（48 根 × 5min = 4 小时）。

    方法改进（v2）：
      - 用日线 OHLC 估计日内波动率参数
      - 开盘 30 分钟附加 2× 波动率（实证：开盘时段波动率约为日内均值的 1.5-2.5 倍）
      - 每根 K 线独立生成 OHLC（非简单路径切分），更接近真实市场微观结构
      - 约束路径终点 = 日线收盘价（布朗桥终点条件）

    参数:
        o, h, l, c: 日线 OHLC
        n_bars: K 线数量（默认 48 = 日内 4 小时 / 5 分钟）
        seed: 随机种子
    返回:
        list[dict]: 每根 K 线的 open/high/low/close/volume
    """
    if seed is not None:
        np.random.seed(seed)

    daily_ret = c / o - 1
    daily_range = (h - l) / o

    # ── 日内波动率校准 ──
    # 中国商品期货典型日内振幅 1.5-3.5%，取 max(实际范围, 1.5%)
    effective_range = max(daily_range, 0.015)
    daily_vol = effective_range / 4.0  # 范围覆盖 ~4σ

    # 每根 K 线的基础波动率
    bar_vol_base = daily_vol / np.sqrt(n_bars)

    # ── 开盘波动率溢价 ──
    # 实证：开盘 30 分钟的波动率是日内均值的 1.5-2.5 倍
    # 这里取 2.0×，模拟真实的开盘波动特征
    opening_boost = 2.0
    boost_bars = 6  # 前 6 根（30 分钟）

    # ── 逐根生成 K 线 ──
    bars = []
    current_price = o
    cum_log_ret = 0.0

    for i in range(n_bars):
        # 波动率：开盘溢价
        bar_vol = bar_vol_base * (opening_boost if i < boost_bars else 1.0)

        # 生成 K 线内收益率（带漂移，向目标收盘价靠拢）
        # 剩余步数
        remaining = n_bars - i
        # 需要实现的剩余对数收益
        target_remaining = daily_ret - cum_log_ret
        # 带漂移的正态分布（均值向目标靠拢）
        drift = target_remaining / remaining if remaining > 0 else 0
        bar_log_ret = np.random.normal(drift, bar_vol)
        cum_log_ret += bar_log_ret

        # K 线 OHLC
        bar_open = current_price
        bar_close = bar_open * np.exp(bar_log_ret)

        # 影线噪声（K 线内部的微观波动）
        bar_high = max(bar_open, bar_close) + abs(bar_open * bar_vol * np.random.uniform(0.1, 0.6))
        bar_low = min(bar_open, bar_close) - abs(bar_open * bar_vol * np.random.uniform(0.1, 0.6))
        bar_high = min(bar_high, h * 1.002)  # 稍超日最高（可接受）
        bar_low = max(bar_low, l * 0.998)    # 稍超日最低（可接受）

        bars.append({
            'open': bar_open, 'high': bar_high,
            'low': bar_low, 'close': bar_close,
            'volume': abs(bar_close - bar_open) * np.random.uniform(200, 1000),
        })
        current_price = bar_close

    # 强制终点匹配日线收盘价
    if bars:
        bars[-1]['close'] = c
        bars[-1]['high'] = max(bars[-1]['high'], c)
        bars[-1]['low'] = min(bars[-1]['low'], c)

    return bars


def run_intraday_backtest(daily_df, daily_tsmom_signal, config, contract_spec):
    """
    日内策略回测引擎。

    流程（每日）：
      1. 模拟本日 48 根 5 分钟 K 线
      2. 取前 6 根（9:00-9:30）分析 → 入场决策
      3. 如有信号，在第 6 根收盘价模拟入场
      4. 逐根后续 K 线更新持仓，直到触发离场或收盘
      5. 记录该日 PnL
      6. 重复 n_simulations 次取平均（降低路径模拟噪声）
    """
    start = pd.Timestamp(config.start_date)
    end = pd.Timestamp(config.end_date)
    df = daily_df.loc[start:end].copy()

    if len(df) < 20:
        print("  [ERR] Not enough daily data")
        return None

    n_sims = config.n_simulations_per_day
    capital = config.initial_capital
    daily_results = []
    all_trades = []
    cons_losses = 0

    print(f"  Running {len(df)} days × {n_sims} simulations ...")

    for date, row in df.iterrows():
        o, h, l, c = row['open'], row['high'], row['low'], row['close']

        # 日线 TSMOM 方向
        daily_dir = 0
        if config.use_daily_filter and daily_tsmom_signal is not None:
            if date in daily_tsmom_signal.index:
                daily_dir = int(daily_tsmom_signal.loc[date])

        # 多路径模拟取平均
        day_pnls = []
        day_trades = []

        for sim in range(n_sims):
            # 生成 5 分钟 K 线
            bars = simulate_5min_bars(o, h, l, c, n_bars=48, seed=sim * 10000 + date.day)

            # 观察期（前 6 根）
            obs_bars = bars[:config.observation_bars]
            obs = analyze_observation_window(obs_bars)

            # 入场决策
            decision = evaluate_entry_signal(obs, daily_dir, config)
            if decision['signal'] == 0:
                day_pnls.append(0.0)
                continue

            # 执行交易
            entry_price = obs.close_price
            direction = decision['signal']
            pos_mgr = PositionManager(config, entry_price, direction, contract_spec)

            # 仓位
            stop_dist = config.hard_stop_pct
            risk_amt = capital * config.risk_per_trade_pct
            multiplier = contract_spec.get('multiplier', 10)
            lots = max(1, int(risk_amt / (stop_dist * entry_price * multiplier)))
            lots = min(lots, 50)  # 上限

            # 保证金检查
            margin_req = entry_price * multiplier * lots * contract_spec.get('margin', 0.10)
            if margin_req > capital * config.max_position_pct:
                lots = max(1, int(capital * config.max_position_pct /
                                  (entry_price * multiplier * contract_spec.get('margin', 0.10))))

            # 模拟持仓过程（从第 7 根 K 线开始）
            remaining_bars = bars[config.observation_bars:]
            exited = False
            exit_price = c
            exit_reason = 'close'

            for j, bar in enumerate(remaining_bars):
                # 时间戳：9:30 + j*5 分钟
                minutes = config.observation_bars * config.bar_interval_min + j * config.bar_interval_min
                hour = 9 + minutes // 60
                minute = minutes % 60
                time_str = f'{hour:02d}:{minute:02d}'

                # 用每根 K 线的 path 更新持仓
                # 模拟 K 线内的价格路径（简化：高→低 顺序取决于方向趋势）
                if direction == 1:
                    check_prices = [bar['open'], bar['low'], bar['high'], bar['close']]
                else:
                    check_prices = [bar['open'], bar['high'], bar['low'], bar['close']]

                for price in check_prices:
                    should_exit, exit_px, reason = pos_mgr.update(price, time_str)
                    if should_exit:
                        exit_price = exit_px
                        exit_reason = reason
                        exited = True
                        break
                if exited:
                    break

            # PnL 计算
            pnl_per_lot = direction * (exit_price - entry_price) * multiplier
            gross_pnl = pnl_per_lot * lots
            cost = config.commission_per_lot * lots * 2
            slippage = config.slippage_ticks * contract_spec.get('tick', 1.0) * lots * 2
            net_pnl = gross_pnl - cost - slippage

            day_pnls.append(net_pnl)
            day_trades.append({
                'date': date, 'direction': direction, 'entry': entry_price,
                'exit': exit_price, 'exit_reason': exit_reason, 'pnl': net_pnl,
                'lots': lots, 'score': decision['score'],
                'confidence': decision['confidence'],
            })

        # 日汇总
        avg_pnl = np.mean(day_pnls)
        day_ret = avg_pnl / capital if capital > 0 else 0

        # 风控：单日最大亏损
        if day_ret < -config.max_daily_loss_pct:
            avg_pnl = -capital * config.max_daily_loss_pct
            day_ret = -config.max_daily_loss_pct

        # 连续亏损处理
        if day_ret < 0:
            cons_losses += 1
        else:
            cons_losses = 0

        if cons_losses >= config.max_cons_losses:
            # 当日停止（收益为 0）
            avg_pnl = 0.0
            day_ret = 0.0

        capital += avg_pnl
        daily_results.append({'date': date, 'pnl': avg_pnl, 'return': day_ret,
                              'capital': capital, 'n_trades': len(day_trades)})
        all_trades.extend(day_trades)

    # 汇总结果
    results = pd.DataFrame(daily_results).set_index('date')
    trades_df = pd.DataFrame(all_trades) if all_trades else pd.DataFrame()

    return {
        'results': results,
        'trades': trades_df,
        'return_series': results['return'],
        'equity_curve': (1 + results['return']).cumprod(),
    }


# ============================================================================
# 第六部分：绩效分析与可视化
# ============================================================================

def compute_metrics(return_series, trades_df=None):
    """计算全套绩效指标"""
    r = return_series
    if len(r) == 0:
        return {}

    n_years = len(r) / 252
    total_ret = (1 + r).prod() - 1
    ann_ret = (1 + total_ret) ** (1 / n_years) - 1 if n_years > 0 else 0
    ann_vol = r.std() * np.sqrt(252)
    sharpe = (ann_ret - 0.02) / ann_vol if ann_vol > 0 else 0

    eq = (1 + r).cumprod()
    dd = eq / eq.expanding().max() - 1
    max_dd = dd.min()
    calmar = ann_ret / abs(max_dd) if max_dd != 0 else 0

    win_rate = (r > 0).mean()
    avg_win = r[r > 0].mean() if (r > 0).any() else 0
    avg_loss = abs(r[r < 0].mean()) if (r < 0).any() else 1e-9

    # 交易统计
    trade_stats = {}
    if trades_df is not None and len(trades_df) > 0:
        td = trades_df
        trade_stats = {
            '总交易次数': len(td),
            '做多占比': f"{(td['direction'] > 0).mean():.0%}",
            '平均得分': f"{td['score'].mean():.1f}",
            '高确信度占比': f"{(td['confidence'] == 'high').mean():.0%}" if 'confidence' in td.columns else 'N/A',
            '离场原因分布': td['exit_reason'].value_counts().to_dict(),
        }

    metrics = {
        '累计收益率':   f'{total_ret:.2%}',
        '年化收益率':   f'{ann_ret:.2%}',
        '年化波动率':   f'{ann_vol:.2%}',
        'Sharpe Ratio': f'{sharpe:.3f}',
        'Calmar Ratio': f'{calmar:.3f}',
        '最大回撤':     f'{max_dd:.2%}',
        '日胜率':       f'{win_rate:.2%}',
        '盈亏比':       f'{avg_win/avg_loss:.2f}',
        **trade_stats,
    }
    return metrics


def plot_results(backtest_result, config):
    """6 面板回测结果图"""
    try:
        import matplotlib.pyplot as plt
        plt.rcParams['font.sans-serif'] = ['SimHei', 'Microsoft YaHei', 'DejaVu Sans']
        plt.rcParams['axes.unicode_minus'] = False
    except ImportError:
        print("matplotlib not available")
        return

    r = backtest_result['return_series']
    eq = backtest_result['equity_curve']
    dd = eq / eq.expanding().max() - 1
    trades = backtest_result['trades']
    metrics = compute_metrics(r, trades)

    fig, axes = plt.subplots(3, 2, figsize=(16, 12))
    fig.suptitle(f'Intraday 5-Min Momentum Strategy | Sharpe={metrics.get("Sharpe Ratio","N/A")}',
                 fontsize=14, fontweight='bold')

    # 1. 净值曲线
    ax = axes[0, 0]
    ax.plot(eq.index, eq.values, 'steelblue', lw=1.5)
    ax.axhline(y=1, color='gray', ls='--', alpha=0.5)
    ax.set_title('Equity Curve (5-min bars simulated from daily OHLC)')
    ax.set_ylabel('NAV'); ax.grid(alpha=0.3)

    # 2. 回撤
    ax = axes[0, 1]
    ax.fill_between(dd.index, 0, dd.values * 100, color='crimson', alpha=0.3)
    ax.plot(dd.index, dd.values * 100, 'darkred', lw=0.5)
    ax.set_title(f'Drawdown (Max: {dd.min():.1%})')
    ax.set_ylabel('%'); ax.grid(alpha=0.3)

    # 3. 日收益分布
    ax = axes[1, 0]
    ax.hist(r * 100, bins=50, color='steelblue', alpha=0.7, edgecolor='white')
    ax.axvline(x=0, color='red', ls='--', alpha=0.5)
    ax.set_title(f'Daily Returns | Skew={r.skew():.2f}')
    ax.set_xlabel('%'); ax.grid(alpha=0.3)

    # 4. 月度收益热力图
    ax = axes[1, 1]
    monthly = r.groupby([r.index.year, r.index.month]).apply(
        lambda x: (1 + x).prod() - 1
    ).unstack()
    if not monthly.empty:
        monthly.columns = [f'{int(c):02d}' for c in monthly.columns]
        im = ax.imshow(monthly * 100, cmap='RdYlGn', aspect='auto', vmin=-6, vmax=6)
        ax.set_xticks(range(len(monthly.columns)))
        ax.set_xticklabels(monthly.columns)
        ax.set_yticks(range(len(monthly.index)))
        ax.set_yticklabels(monthly.index)
        ax.set_title('Monthly Returns (%)')
        plt.colorbar(im, ax=ax, label='%')

    # 5. 离场原因分布
    ax = axes[2, 0]
    if len(trades) > 0:
        reasons = trades['exit_reason'].value_counts()
        colors = {'hard_stop': 'crimson', 'trailing_stop': 'forestgreen',
                  'force_exit': 'gray', 'midday_cut': 'orange', 'close': 'steelblue'}
        bar_colors = [colors.get(r, 'gray') for r in reasons.index]
        ax.bar(range(len(reasons)), reasons.values, color=bar_colors)
        ax.set_xticks(range(len(reasons)))
        ax.set_xticklabels(reasons.index, rotation=30, ha='right')
        ax.set_title('Exit Reason Distribution')
        ax.set_ylabel('Count'); ax.grid(alpha=0.3, axis='y')

    # 6. 指标面板
    ax = axes[2, 1]
    ax.axis('off')
    for j, (k, v) in enumerate(metrics.items()):
        ax.text(0.05, 0.96 - j * 0.06, f'{k}:', fontsize=8, fontweight='bold',
                transform=ax.transAxes, va='top')
        val_str = str(v)
        if isinstance(v, dict):
            val_str = ', '.join(f'{kk}:{vv}' for kk, vv in list(v.items())[:3])
        ax.text(0.55, 0.96 - j * 0.06, val_str, fontsize=8,
                transform=ax.transAxes, va='top', family='monospace')

    plt.tight_layout()
    path = 'intraday_5min_results.png'
    plt.savefig(path, dpi=150, bbox_inches='tight')
    print(f"\n  Chart saved: {path}")
    plt.show()


# ============================================================================
# 第七部分：主程序
# ============================================================================

def main():
    print("=" * 65)
    print("  日内 5 分钟动量策略：30min 定方向，日内平仓")
    print("  Based on Ernie Chan — Intraday Momentum")
    print("=" * 65)

    config = IntradayConfig()
    spec = CONTRACT_SPECS['RB']

    # ── 1. 获取日线数据 + 计算 TSMOM ──
    print("\n[1/4] Loading daily data ...")
    from futures_momentum_backtest import (
        fetch_futures_daily, _parse_kline, compute_momentum_signal,
    )

    raw = fetch_futures_daily("RB0")
    if not raw:
        print("  Failed. Exiting.")
        return

    daily = _parse_kline(raw)
    daily['tsmom'] = compute_momentum_signal(daily['close'], config.tsmom_lookback_days)
    print(f"  RB0: {len(daily)} days")

    # ── 2. 演示模拟 5 分钟 K 线 ──
    print("\n[2/4] Simulating 5-min bars (demo) ...")
    last = daily.iloc[-1]
    demo_bars = simulate_5min_bars(last['open'], last['high'],
                                    last['low'], last['close'], n_bars=48, seed=42)
    # 取前 6 根分析
    obs = analyze_observation_window(demo_bars[:6])
    decision = evaluate_entry_signal(obs, daily['tsmom'].iloc[-1], config)
    print(f"  观察期分析:")
    print(f"    30min 涨跌: {obs.ret_30min:.4f}  |  阳线: {obs.n_bullish}/6")
    print(f"    连续性: {obs.continuity:.0%}  |  动能比: {obs.acceleration:.2f}")
    print(f"    最大不利偏移: {obs.max_adverse:.4f}")
    print(f"  入场决策: signal={decision['signal']:+d}  score={decision['score']:.0f}")
    if decision['reject_reasons']:
        print(f"  拒绝原因: {decision['reject_reasons']}")

    # ── 3. 回测 ──
    print(f"\n[3/4] Running backtest ({config.start_date} → {config.end_date}) ...")
    result = run_intraday_backtest(daily, daily['tsmom'], config, spec)

    if result is None:
        print("  Backtest failed.")
        return

    # ── 4. 绩效 ──
    print(f"\n[4/4] Performance Metrics")
    print("-" * 60)
    for k, v in compute_metrics(result['return_series'], result['trades']).items():
        if not isinstance(v, dict):
            print(f"  {k:.<28} {v}")

    # 年度统计
    r = result['return_series']
    print(f"\n  Annual Breakdown:")
    print(f"  {'Year':<8} {'Return':>9} {'Sharpe':>8} {'Max DD':>9} {'Win Rate':>9}")
    print(f"  {'-'*48}")
    for yr, grp in r.groupby(r.index.year):
        yr_ret = (1 + grp).prod() - 1
        yr_vol = grp.std() * np.sqrt(252)
        yr_sharpe = (grp.mean()*252 - 0.02) / yr_vol if yr_vol > 0 else 0
        yr_dd = ((1+grp).cumprod() / (1+grp).cumprod().expanding().max() - 1).min()
        yr_wr = (grp > 0).mean()
        print(f"  {yr:<8} {yr_ret:>8.2%} {yr_sharpe:>8.2f} {yr_dd:>8.2%} {yr_wr:>8.1%}")

    # 图表
    plot_results(result, config)

    print(f"\n{'='*65}")
    print("  ⚠ 数据说明：")
    print("  以上结果基于'模拟 5 分钟 K 线'（布朗桥 + 日线 OHLC 约束）。")
    print("  免费 API 不提供历史分钟数据，模拟路径用于策略逻辑验证。")
    print("  真实日内回测数据源：")
    print("    • RiceQuant / JoinQuant（免费分钟级历史数据）")
    print("    • TuShare Pro (tushare.pro)")
    print("    • AKShare (akshare)")
    print(f"{'='*65}")


if __name__ == '__main__':
    main()
