#!/usr/bin/env python3
"""
日内动量策略：开盘 30 分钟走势预测收盘方向
=============================================
基于 Ernie Chan 的实证研究（《Algorithmic Trading》第 3 章 + 个人研究论文）：

核心发现（Chan 的 intraday momentum）：
  开盘第一个 30 分钟的走势方向，对剩余交易时段（30min → close）有统计上
  显著的预测能力。这不是"均值回归"而是"日内趋势延续"——开盘点明了方向，
  剩余时间沿着这个方向走。

策略逻辑：
  ① 9:00 开盘（期货集合竞价出开盘价）
  ② 9:30 计算：ret_30min = P_9:30 / P_open - 1
  ③ ret_30min > +threshold → 做多，持有至收盘
  ④ ret_30min < -threshold → 做空，持有至收盘
  ⑤ |ret_30min| < threshold  → 不交易（信号不够强）

日线 TSMOM 过滤器（与现有日线策略叠加）：
  日线动量方向 ↑ + 日内信号 ↑ → 做多（确认）
  日线动量方向 ↓ + 日内信号 ↓ → 做空（确认）
  方向不一致 → 不做（降低假突破概率）

风控（日内交易核心）：
  - 硬止损：入场后反向 0.5% 立即离场
  - 时间止损：11:30 前仍无利润 → 成本价平仓
  - 强制离场：14:55 必须平仓（不隔夜）
  - 单日最大亏损：总资金 2%（达到则当日停止）

重要说明：
  历史回测使用"日内路径模拟"（布朗桥 + 日线 OHLC 约束），因为免费 API
  不提供历史分钟数据。实盘/纸交易使用实时 1 分钟数据（每日采集）。
"""

import urllib.request
import re
import json
import time
import os
import warnings
from datetime import datetime, timedelta
from collections import defaultdict
import numpy as np
import pandas as pd

warnings.filterwarnings('ignore')

# ============================================================================
# 配置
# ============================================================================

INTRADAY_CONFIG = {
    # ── 信号参数 ──
    'first_bar_minutes': 30,           # 开盘后多少分钟形成信号
    'signal_threshold': 0.001,         # 信号阈值（0.1%），低于此值不交易
    'use_daily_tsmom_filter': True,    # 是否用日线 TSMOM 过滤方向
    'tsmom_lookback_months': 12,       # 日线 TSMOM 回看期

    # ── 风控 ──
    'hard_stop_pct': 0.005,            # 硬止损 0.5%
    'time_stop_session': '11:30',      # 时间止损点（上午收盘前）
    'force_exit_time': '14:55',         # 强制平仓时间
    'max_daily_loss_pct': 0.02,        # 单日最大亏损（总资金%）
    'max_consecutive_losses': 3,       # 连续亏损后当日停止

    # ── 仓位 ──
    'risk_per_trade_pct': 0.005,       # 每笔风险 0.5%（总资金%）
    'initial_capital': 1_000_000,

    # ── 回测 ──
    'start_date': '2022-01-01',
    'end_date':   '2026-07-25',
    'commission_per_lot': 10,          # 每手手续费（元）
    'slippage_ticks': 1,              # 滑点（跳）

    # ── 合约规格（示例：螺纹钢 RB） ──
    'contract_multiplier': 10,         # 每手 10 吨
    'tick_size': 1.0,                  # 最小变动价位 1 元
    'margin_rate': 0.10,              # 保证金比例 10%

    # ── 数据采集 ──
    'min_data_dir': 'data/intraday_min',  # 分钟数据存储目录
}


# 期货品种的合约规格（用于仓位计算）
CONTRACT_SPECS = {
    'RB': {'multiplier': 10, 'tick_size': 1.0,   'margin': 0.10, 'name': '螺纹钢'},
    'JM': {'multiplier': 60, 'tick_size': 0.5,   'margin': 0.12, 'name': '焦煤'},
    'CU': {'multiplier': 5,  'tick_size': 10.0,  'margin': 0.10, 'name': '沪铜'},
    'AU': {'multiplier': 1000,'tick_size': 0.02, 'margin': 0.08, 'name': '沪金'},
    'SC': {'multiplier': 1000,'tick_size': 0.1,  'margin': 0.12, 'name': '原油'},
    'I':  {'multiplier': 100,'tick_size': 0.5,   'margin': 0.12, 'name': '铁矿石'},
    'M':  {'multiplier': 10, 'tick_size': 1.0,   'margin': 0.08, 'name': '豆粕'},
    'MA': {'multiplier': 10, 'tick_size': 1.0,   'margin': 0.08, 'name': '甲醇'},
}

# ============================================================================
# 第一部分：分钟数据获取（实盘/纸交易用）
# ============================================================================

def fetch_today_minute_bars(symbol_base):
    """
    从新浪获取当日期货 1 分钟 K 线。

    返回格式（新浪 API）：
      每个 bar 是一个 list: [time, open, close, volume, open_interest, prev_settle?, date?]
      首 bar 包含日期和昨结算，后续 bar 只有 5 个字段。

    注意：该 API 只返回最近一个交易时段的分钟数据（夜盘+日盘），
          不能用于历史回测。仅用于每日采集做前向测试。
    """
    # 使用连续合约符号
    sym = f"{symbol_base}0"
    url = (
        f"https://stock2.finance.sina.com.cn/futures/api/jsonp.php/"
        f"var%20m1_{sym}=/InnerFuturesNewService.getMinLine?symbol={sym}&type=1"
    )
    try:
        req = urllib.request.Request(url)
        req.add_header("User-Agent", "Mozilla/5.0")
        data = urllib.request.urlopen(req, timeout=10).read().decode()
        m = re.search(r'\((\[.*\])\)', data, re.DOTALL)
        if m:
            records = json.loads(m.group(1))
            return _parse_minute_bars(records)
    except Exception as e:
        print(f"  [ERR] fetch {sym}: {e}")
    return pd.DataFrame()


def _parse_minute_bars(records):
    """解析新浪分钟数据 → DataFrame"""
    rows = []
    current_date = None

    for r in records:
        if len(r) >= 7:
            # 首 bar：包含日期
            time_str, o, c, v, oi, prev_settle, date_str = r[0], r[1], r[2], r[3], r[4], r[5], r[6]
            current_date = date_str
        elif len(r) >= 5:
            time_str, o, c, v, oi = r[0], r[1], r[2], r[3], r[4]
        else:
            continue

        if current_date:
            dt = pd.Timestamp(f"{current_date} {time_str}")
        else:
            continue

        rows.append({
            'datetime': dt,
            'open': float(o),
            'close': float(c),
            'volume': float(v),
            'open_interest': float(oi),
        })

    df = pd.DataFrame(rows)
    if not df.empty:
        df = df.set_index('datetime').sort_index()
    return df


def collect_daily_min_data(symbols, save_dir='data/intraday_min'):
    """
    每日收盘后采集分钟数据（用于积累前向测试数据集）。

    建议：设置 cron 任务在 15:05 运行此函数。
    """
    os.makedirs(save_dir, exist_ok=True)
    today = datetime.now().strftime('%Y-%m-%d')

    for sym in symbols:
        df = fetch_today_minute_bars(sym)
        if df.empty:
            print(f"  {sym}: no data")
            continue

        fname = f"{sym}_{today}.parquet"
        path = os.path.join(save_dir, fname)
        df.to_parquet(path)
        print(f"  {sym}: saved {len(df)} bars → {path}")


# ============================================================================
# 第二部分：日内信号生成
# ============================================================================

def compute_intraday_signal(minute_df, config):
    """
    从分钟数据中提取"开盘 30 分钟动量信号"。

    逻辑（Chan 的研究）：
      ret_30min = (P_30min / P_open) - 1
      signal = sign(ret_30min)  if |ret_30min| > threshold else 0

    对于中国期货（9:00 开盘）：
      - P_open = 9:00 的第一根 K 线开盘价（或集合竞价价）
      - P_30min = 9:30 那根 K 线的收盘价（或 9:00-9:30 区间最后一根 K 线）

    参数:
        minute_df: 分钟 DataFrame（datetime index，含 open, close）
        config: 配置字典
    返回:
        tuple: (signal, ret_30min, open_price, price_at_signal)
               signal: +1=做多, -1=做空, 0=不交易
    """
    if minute_df.empty:
        return 0, 0.0, 0.0, 0.0

    # 找到日盘开盘时段（9:00 前后）
    # 中国期货日盘 9:00 开盘
    day_open = minute_df.between_time('08:55', '09:05')
    if day_open.empty:
        return 0, 0.0, 0.0, 0.0

    open_price = day_open.iloc[0]['open']

    # 找到 9:30 附近的 K 线
    signal_time = minute_df.between_time('09:28', '09:32')
    if signal_time.empty:
        # 如果精确 30 分钟找不到，取 9:00 后第 30 分钟
        after_open = minute_df[minute_df.index.time >= pd.Timestamp('09:00').time()]
        if len(after_open) < config['first_bar_minutes']:
            return 0, 0.0, open_price, open_price
        signal_bar = after_open.iloc[config['first_bar_minutes'] - 1]
    else:
        signal_bar = signal_time.iloc[0]

    price_at_signal = signal_bar['close']
    ret_30min = price_at_signal / open_price - 1

    threshold = config['signal_threshold']
    if abs(ret_30min) < threshold:
        signal = 0
    else:
        signal = np.sign(ret_30min)

    return int(signal), ret_30min, open_price, price_at_signal


# ============================================================================
# 第三部分：日内路径模拟（用于历史回测）
# ============================================================================

def simulate_intraday_path(open_price, high, low, close, n_steps=240, seed=None):
    """
    用布朗桥 + 日线 OHLC 约束模拟日内价格路径。

    原理：
      1. 生成一个从 Open → Close 的布朗桥（保证终点匹配收盘价）
      2. 缩放到日内的 High-Low 范围（保证极值一致）
      3. 从路径中提取 9:30 的价格作为"模拟 30 分钟信号"

    这是金融工程中常用的方法（Glasserman, 2004），Chan 本人也认可
    蒙特卡洛方法在策略验证中的应用。

    参数:
        open_price, high, low, close: 日线 OHLC
        n_steps: 日内步数（240 = 4小时 × 60分钟）
        seed: 随机种子（None = 不固定）
    返回:
        np.array: 长度为 n_steps+1 的价格路径（从 open 开始，到 close 结束）
    """
    if seed is not None:
        np.random.seed(seed)

    # 日收益率
    daily_ret = close / open_price - 1
    daily_range = (high - low) / open_price  # 日内振幅

    # 生成标准布朗桥
    t = np.linspace(0, 1, n_steps + 1)
    W = np.zeros(n_steps + 1)
    for i in range(1, n_steps + 1):
        dt = t[i] - t[i-1]
        W[i] = W[i-1] + np.random.normal(0, np.sqrt(dt))

    # 布朗桥：B(t) = W(t) - t × W(1) + t × target
    bridge = W - t * W[-1] + t * daily_ret

    # 缩放到日内范围
    # 日波动率 ≈ daily_range / 4（假设范围覆盖约 4 个标准差）
    daily_vol = daily_range / 4 if daily_range > 0 else abs(daily_ret) / 2
    if daily_vol <= 0:
        daily_vol = 0.005  # 最小假设波动 0.5%

    # 缩放布朗桥到目标波动
    bridge_vol = np.std(bridge)
    if bridge_vol > 0:
        bridge = bridge * (daily_vol / bridge_vol)

    # 构建价格路径
    prices = open_price * np.exp(bridge)

    # 约束到 High/Low 范围
    day_high = max(high, open_price, close)
    day_low = min(low, open_price, close)
    prices = np.clip(prices, day_low, day_high)

    # 确保终点 = close
    prices[-1] = close

    return prices


def estimate_first_30min_return(open_price, high, low, close, n_simulations=100):
    """
    通过蒙特卡洛模拟估计"开盘 30 分钟收益率"的分布。

    每次模拟生成一条日内路径，提取第 30 分钟的价格，
    然后对多次模拟取平均。

    返回:
        dict: {'mean_ret': ..., 'std_ret': ..., 'prob_up': ..., 'prob_down': ...}
    """
    returns_30min = []

    for i in range(n_simulations):
        path = simulate_intraday_path(open_price, high, low, close, n_steps=240, seed=i)
        # 第 30 步 ≈ 9:30（240 步/4小时 = 60 步/小时 → 30 步/30分钟）
        idx_30min = min(30, len(path) - 2)
        p_30 = path[idx_30min]
        ret = p_30 / open_price - 1
        returns_30min.append(ret)

    rets = np.array(returns_30min)
    return {
        'mean_ret': np.mean(rets),
        'std_ret': np.std(rets),
        'prob_up': (rets > 0).mean(),
        'prob_down': (rets < 0).mean(),
        'median_ret': np.median(rets),
    }


# ============================================================================
# 第四部分：日内回测引擎（模拟路径版本）
# ============================================================================

class IntradayBacktest:
    """
    日内动量策略回测引擎。

    因为免费 API 没有历史分钟数据，回测使用"日内路径模拟"：
      - 每日根据日线 OHLC 模拟 100 条可能的日内路径
      - 每条路径独立执行策略
      - 汇总所有路径的结果取平均（降低模拟噪声）

    用于策略逻辑验证和参数敏感度分析。
    """

    def __init__(self, config, contract_spec=None):
        self.config = config
        self.spec = contract_spec or CONTRACT_SPECS.get('RB', {})
        self.trade_log = []
        self.daily_results = []

    def run_on_daily_data(self, daily_df, daily_tsmom_signal=None):
        """
        在日线数据上运行日内策略（模拟路径版本）。

        参数:
            daily_df: 日线 DataFrame（OHLC）
            daily_tsmom_signal: 日线 TSMOM 信号 Series（+1/-1/0），
                               用于过滤日内信号方向。
        """
        config = self.config
        start = pd.Timestamp(config['start_date'])
        end = pd.Timestamp(config['end_date'])
        df = daily_df.loc[start:end].copy()

        if len(df) < 20:
            print("  [ERR] Not enough daily data")
            return None

        n_paths = 50  # 每天模拟 50 条路径（平衡精度和速度）
        capital = config['initial_capital']
        daily_pnl = []

        for i, (date, row) in enumerate(df.iterrows()):
            o, h, l, c = row['open'], row['high'], row['low'], row['close']

            # 日线 TSMOM 过滤
            daily_dir = 0  # 0=无偏好, +1=偏多, -1=偏空
            if config['use_daily_tsmom_filter'] and daily_tsmom_signal is not None:
                if date in daily_tsmom_signal.index:
                    daily_dir = int(daily_tsmom_signal.loc[date])

            # 模拟多条日内路径，取平均 PnL
            path_pnls = []
            for seed in range(n_paths):
                pnl = self._simulate_one_day(o, h, l, c, daily_dir, capital, seed)
                path_pnls.append(pnl)

            day_pnl = np.mean(path_pnls)
            day_ret = day_pnl / capital if capital > 0 else 0

            # 风控：单日最大亏损
            if day_ret < -config['max_daily_loss_pct']:
                day_pnl = -capital * config['max_daily_loss_pct']
                day_ret = -config['max_daily_loss_pct']

            capital += day_pnl
            daily_pnl.append({'date': date, 'pnl': day_pnl, 'return': day_ret, 'capital': capital})

        # 存储结果
        self.results = pd.DataFrame(daily_pnl).set_index('date')
        self.return_series = self.results['return']
        self.equity_curve = (1 + self.return_series).cumprod()
        return self.return_series

    def _simulate_one_day(self, o, h, l, c, daily_dir, capital, seed):
        """模拟一天的交易（单条路径）"""
        config = self.config
        path = simulate_intraday_path(o, h, l, c, n_steps=240, seed=seed)

        # 提取 30 分钟信号
        idx_30 = min(30, len(path) - 2)
        p_30 = path[idx_30]
        ret_30 = p_30 / o - 1

        threshold = config['signal_threshold']
        if abs(ret_30) < threshold:
            return 0.0

        intraday_signal = np.sign(ret_30)

        # TSMOM 过滤器
        if config['use_daily_tsmom_filter'] and daily_dir != 0:
            if intraday_signal != daily_dir:
                return 0.0  # 方向不一致，不做

        # 仓位计算
        stop_distance = config['hard_stop_pct']
        risk_amount = capital * config['risk_per_trade_pct']
        price_per_point = self.spec.get('multiplier', 10)
        lots = max(0, int(risk_amount / (stop_distance * o * price_per_point)))
        if lots < 1:
            lots = 1  # 最少 1 手（简化）
        if lots > 50:
            lots = 50  # 上限

        # 检查保证金是否足够（简化：用开仓价的保证金）
        margin_required = o * price_per_point * lots * self.spec.get('margin', 0.10)
        if margin_required > capital * 0.25:  # 单品种不超过 25%
            lots = max(1, int(capital * 0.25 / (o * price_per_point * self.spec.get('margin', 0.10))))

        entry_price = p_30
        stop_price = entry_price * (1 - intraday_signal * stop_distance)

        # 模拟持仓期间的价格路径
        # 剩余价格路径（从 30 min 到收盘）
        remaining_path = path[idx_30:]

        exit_price = None
        exit_reason = None

        for price in remaining_path[1:]:  # 跳过 entry 点
            # 检查止损
            if intraday_signal == 1 and price <= stop_price:
                exit_price = stop_price
                exit_reason = 'hard_stop'
                break
            elif intraday_signal == -1 and price >= stop_price:
                exit_price = stop_price
                exit_reason = 'hard_stop'
                break

        # 未触发止损 → 收盘平仓
        if exit_price is None:
            exit_price = c
            exit_reason = 'close'

        # 计算 PnL
        pnl_per_lot = intraday_signal * (exit_price - entry_price) * price_per_point
        gross_pnl = pnl_per_lot * lots

        # 扣除成本
        cost = config['commission_per_lot'] * lots * 2  # 双边手续费
        slippage = config['slippage_ticks'] * self.spec.get('tick_size', 1.0) * lots * 2
        net_pnl = gross_pnl - cost - slippage

        return net_pnl

    def get_metrics(self):
        """计算回测绩效指标"""
        r = self.return_series
        if r is None or len(r) == 0:
            return {}

        n_days = len(r)
        n_years = n_days / 252
        total_ret = self.equity_curve.iloc[-1] - 1
        ann_ret = (1 + total_ret) ** (1 / n_years) - 1 if n_years > 0 else 0
        ann_vol = r.std() * np.sqrt(252)
        sharpe = (ann_ret - 0.02) / ann_vol if ann_vol > 0 else 0

        # 最大回撤
        running_max = self.equity_curve.expanding().max()
        dd = self.equity_curve / running_max - 1
        max_dd = dd.min()

        # 胜率
        win_rate = (r > 0).mean()
        avg_win = r[r > 0].mean() if (r > 0).any() else 0
        avg_loss = abs(r[r < 0].mean()) if (r < 0).any() else 1e-9

        # 交易的月份统计
        monthly = r.resample('ME').apply(lambda x: (1 + x).prod() - 1)
        pos_months = (monthly > 0).mean()

        return {
            '累计收益率':   f'{total_ret:.2%}',
            '年化收益率':   f'{ann_ret:.2%}',
            '年化波动率':   f'{ann_vol:.2%}',
            'Sharpe Ratio': f'{sharpe:.3f}',
            '最大回撤':     f'{max_dd:.2%}',
            '日胜率':       f'{win_rate:.2%}',
            '盈亏比':       f'{avg_win/avg_loss:.2f}',
            '月胜率':       f'{pos_months:.1%}',
            '交易天数':     n_days,
            '数据说明':     '模拟日内路径（基于日线OHLC + 布朗桥）',
        }


# ============================================================================
# 第五部分：当日实盘信号生成（非回测）
# ============================================================================

def generate_live_signal(symbol_base, config):
    """
    基于实时分钟数据生成当日交易信号。

    用法（盘中 9:30 调用）：
      signal, info = generate_live_signal('RB', INTRADAY_CONFIG)
      if signal != 0:
          # 执行交易
          ...

    返回:
        signal: +1=做多, -1=做空, 0=不动
        info: dict 含详细信号信息
    """
    df = fetch_today_minute_bars(symbol_base)
    if df.empty:
        return 0, {'error': 'No minute data available'}

    signal, ret_30, open_p, sig_p = compute_intraday_signal(df, config)

    # 获取日线 TSMOM 方向（此处需要已计算好的日线信号）
    daily_dir = 0  # 实际使用时从日线信号数据库读取

    # 过滤器
    if config['use_daily_tsmom_filter'] and daily_dir != 0:
        if signal != daily_dir:
            signal = 0

    info = {
        'symbol': symbol_base,
        'signal': signal,
        'ret_30min': ret_30,
        'open_price': open_p,
        'signal_price': sig_p,
        'daily_tsmom_dir': daily_dir,
        'timestamp': datetime.now().isoformat(),
    }

    return signal, info


# ============================================================================
# 第六部分：可视化
# ============================================================================

def plot_intraday_results(bt, config):
    """绘制日内策略回测结果"""
    try:
        import matplotlib.pyplot as plt
        plt.rcParams['font.sans-serif'] = ['SimHei', 'Microsoft YaHei', 'DejaVu Sans']
        plt.rcParams['axes.unicode_minus'] = False
    except ImportError:
        print("matplotlib not installed — skipping charts.")
        return

    r = bt.return_series
    if r is None or len(r) == 0:
        return

    eq = bt.equity_curve
    dd = eq / eq.expanding().max() - 1

    fig, axes = plt.subplots(2, 2, figsize=(14, 10))
    fig.suptitle(
        f'Intraday Momentum (First-30-Min) | '
        f'Sharpe={bt.get_metrics().get("Sharpe Ratio","N/A")}',
        fontsize=13, fontweight='bold',
    )

    # 1. 净值曲线
    ax = axes[0, 0]
    ax.plot(eq.index, eq.values, 'steelblue', lw=1.5, label='Intraday Momentum')
    ax.axhline(y=1, color='gray', ls='--', alpha=0.5)
    ax.set_title('Equity Curve (Simulated Intraday Paths)')
    ax.set_ylabel('NAV'); ax.legend(); ax.grid(alpha=0.3)

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

    # 4. 指标
    ax = axes[1, 1]
    ax.axis('off')
    for j, (k, v) in enumerate(bt.get_metrics().items()):
        ax.text(0.05, 0.94 - j*0.08, f'{k}:', fontsize=9, fontweight='bold',
                transform=ax.transAxes, va='top')
        ax.text(0.55, 0.94 - j*0.08, str(v), fontsize=9,
                transform=ax.transAxes, va='top', family='monospace')

    plt.tight_layout()
    path = 'intraday_momentum_results.png'
    plt.savefig(path, dpi=150, bbox_inches='tight')
    print(f"\n  Chart saved: {path}")
    plt.show()


# ============================================================================
# 第七部分：演示——日内路径模拟可视化
# ============================================================================

def demo_intraday_paths(open_price=3086, high=3100, low=3060, close=3092):
    """
    演示：展示 5 条模拟的日内价格路径，直观理解策略逻辑。
    """
    try:
        import matplotlib.pyplot as plt
        plt.rcParams['font.sans-serif'] = ['SimHei', 'Microsoft YaHei', 'DejaVu Sans']
        plt.rcParams['axes.unicode_minus'] = False
    except ImportError:
        print("matplotlib required for demo")
        return

    fig, axes = plt.subplots(1, 2, figsize=(14, 5))

    # 左图：多条模拟路径
    ax = axes[0]
    times = [f'{h:02d}:{m:02d}' for h in range(9, 15) for m in range(0, 60, 6)]
    times = times[:241]  # 240 + 1 points

    colors = plt.cm.tab10(range(5))
    for i in range(5):
        path = simulate_intraday_path(open_price, high, low, close, seed=i)
        if len(path) <= len(times):
            x = range(len(path))
        else:
            x = range(len(times))
            path = path[:len(times)]
        ax.plot(x, path, color=colors[i], lw=0.8, alpha=0.7, label=f'Path {i+1}')

    # 标记关键时间点
    ax.axvline(x=30, color='orange', ls='--', alpha=0.7, label='9:30 (Signal)')
    ax.axhline(y=open_price, color='gray', ls=':', alpha=0.4, label=f'Open={open_price}')
    ax.set_xlabel('Minutes from 9:00 Open')
    ax.set_ylabel('Price')
    ax.set_title('Simulated Intraday Paths (Brownian Bridge + OHLC)')
    ax.legend(fontsize=8); ax.grid(alpha=0.3)

    # 右图：30 分钟收益率分布
    ax = axes[1]
    rets = []
    for i in range(200):
        path = simulate_intraday_path(open_price, high, low, close, seed=i)
        p30 = path[min(30, len(path)-2)]
        rets.append(p30 / open_price - 1)

    ax.hist(np.array(rets) * 100, bins=30, color='steelblue', alpha=0.7, edgecolor='white')
    ax.axvline(x=0, color='red', ls='--', alpha=0.5)
    ax.axvline(x=np.mean(rets)*100, color='green', ls='-', alpha=0.7,
               label=f'Mean={np.mean(rets)*100:.2f}%')
    ax.set_xlabel('First-30-Min Return (%)')
    ax.set_ylabel('Frequency (200 simulations)')
    ax.set_title(f'Distribution of Simulated 30-Min Returns\n'
                 f'Actual Day: O={open_price} H={high} L={low} C={close}')
    ax.legend(); ax.grid(alpha=0.3)

    plt.tight_layout()
    path = 'intraday_path_demo.png'
    plt.savefig(path, dpi=150, bbox_inches='tight')
    print(f"Demo chart saved: {path}")
    plt.show()


# ============================================================================
# 第八部分：主程序
# ============================================================================

def main():
    print("=" * 65)
    print("  日内动量策略：开盘 30 分钟 → 收盘方向")
    print("  Ernie Chan — Intraday Momentum Research")
    print("=" * 65)

    # ── 1. 加载日线数据 ──
    print("\n[1/4] Loading daily data for backtest ...")
    from futures_momentum_backtest import (
        fetch_futures_daily, _parse_kline, CONFIG as DAILY_CONFIG,
        compute_momentum_signal,
    )

    TEST_SYMBOL = "RB0"
    raw = fetch_futures_daily(TEST_SYMBOL)
    if not raw:
        print("  Failed to fetch data. Exiting.")
        return

    daily_df = _parse_kline(raw)
    print(f"  {TEST_SYMBOL}: {len(daily_df)} days")

    # 计算日线 TSMOM 作为过滤器
    daily_df['daily_ret'] = daily_df['close'].pct_change()
    lookback = INTRADAY_CONFIG['tsmom_lookback_months'] * 21
    daily_df['tsmom_signal'] = compute_momentum_signal(daily_df['close'], lookback)

    # ── 2. 演示日内路径模拟 ──
    print("\n[2/4] Demonstrating intraday path simulation ...")
    last_row = daily_df.iloc[-1]
    demo_intraday_paths(
        open_price=last_row['open'],
        high=last_row['high'],
        low=last_row['low'],
        close=last_row['close'],
    )

    # ── 3. 运行日内策略回测 ──
    print("\n[3/4] Running intraday backtest (simulated paths) ...")
    bt = IntradayBacktest(INTRADAY_CONFIG, CONTRACT_SPECS.get('RB'))
    ret = bt.run_on_daily_data(daily_df, daily_df['tsmom_signal'])

    if ret is None:
        print("  Backtest failed.")
        return

    # ── 4. 绩效 ──
    print("\n[4/4] Performance Metrics (SIMULATED — for strategy logic validation only)")
    print("-" * 60)
    for k, v in bt.get_metrics().items():
        print(f"  {k:.<28} {v}")

    # 分年统计
    print(f"\n  Annual Breakdown:")
    print(f"  {'Year':<8} {'Return':>9} {'Sharpe':>8} {'Max DD':>9}")
    print(f"  {'-'*40}")
    for yr, grp in ret.groupby(ret.index.year):
        yr_ret = (1 + grp).prod() - 1
        yr_vol = grp.std() * np.sqrt(252)
        yr_sharpe = (grp.mean()*252 - 0.02) / yr_vol if yr_vol > 0 else 0
        yr_dd = ((1+grp).cumprod() / (1+grp).cumprod().expanding().max() - 1).min()
        print(f"  {yr:<8} {yr_ret:>8.2%} {yr_sharpe:>8.2f} {yr_dd:>8.2%}")

    # 图表
    plot_intraday_results(bt, INTRADAY_CONFIG)

    print(f"\n{'='*65}")
    print("  ⚠ 重要提示：")
    print("  以上回测结果基于'日内路径模拟'（布朗桥 + 日线 OHLC），")
    print("  用于验证策略逻辑，不等同于真实日内回测。")
    print("  免费 API 不提供历史分钟数据。获取真实回测数据可用：")
    print("    - RiceQuant / JoinQuant 量化平台（免费历史分钟数据）")
    print("    - TuShare Pro (tushare.pro) 分钟数据接口")
    print("    - AKShare (akshare) 期货分钟数据")
    print(f"  实盘前向测试：每日 15:05 运行 collect_daily_min_data() 积累数据")
    print(f"{'='*65}")


if __name__ == '__main__':
    main()
