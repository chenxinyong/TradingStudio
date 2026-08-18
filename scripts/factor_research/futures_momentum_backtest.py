#!/usr/bin/env python3
"""
期货时间序列动量策略回测系统 (Time-Series Momentum)
====================================================
基于 Ernie Chan《Algorithmic Trading: Winning Strategies and Their Rationale》

策略核心（Chan 第3章）：
  时间序列动量 (TSMOM)：做多过去 N 个月上涨的品种，做空过去 N 个月下跌的品种。
  ——"上涨就做多、下跌就做空"，不比较横截面排名。

关键设计（来自 Chan 的研究发现）：
  1. 信号：sign(过去 N 个月收益)，N ∈ {1, 3, 6, 12}
  2. 仓位：波动率倒数加权（风险平价），每个品种贡献相等风险
  3. 组合：多品种多板块分散化 —— 时间序列动量最大的优势是在危机期间表现好
  4. 止损：Chan 不设传统止损；他通过波动率目标 + 月度调仓来控制回撤
  5. 退出：滚动 2 年 Sharpe < 1.0 → 考虑退场（Chan 的策略生命周期管理）

期货市场的天然优势（Chan 反复强调）：
  - 天然双向交易，做空无障碍（股票做空需要借券 + 成本高）
  - 多板块低相关性（贵金属、能源、农产品、黑色之间相关性远低于股票行业之间）
  - 杠杆可控，通过保证金比例精确管理风险敞口

参考文献：
  - Chan, E. (2013). Algorithmic Trading: Winning Strategies and Their Rationale. Wiley.
  - Moskowitz, Ooi & Pedersen (2012). Time Series Momentum. JFE.

数据源：新浪财经期货 API（与现有 analyze_jm.py 保持一致）
"""

import urllib.request
import re
import json
import time
import warnings
from datetime import datetime
from itertools import product
import numpy as np
import pandas as pd

warnings.filterwarnings('ignore')

# ============================================================================
# 第一部分：全局配置
# ============================================================================

CONFIG = {
    # ── 信号参数 ──
    # Chan 建议测试 1/3/6/12 个月，12个月是经典参数
    'lookback_months': [1, 3, 6, 12],
    'primary_lookback': 12,           # 主回看期（月）
    'vol_half_life': 60,              # 波动率估计 EWMA 半衰期（天）
    'target_vol': 0.15,               # 每品种目标年化波动率 15%

    # ── 调仓频率 ──
    # Chan: 月度调仓最佳 —— 频率太高交易成本侵蚀，太低反应迟钝
    'rebalance_freq': 'monthly',      # 'daily' | 'weekly' | 'monthly'

    # ── 成本 ──
    # 中国期货：交易所手续费约万0.2~万1 + 经纪商加收 ≈ 万0.5~万2 单边
    # 滑点：活跃合约通常 1 tick ≈ 0.02%~0.05%
    # 综合单边成本取 0.08%（保守估计，含滑点）
    'one_way_cost_bps': 8,            # 单边综合成本（bp）

    # ── 回测区间 ──
    'start_date': '2022-01-01',
    'end_date':   '2026-07-25',

    # ── 资金管理 ──
    'initial_capital': 1_000_000,     # 100万
    'leverage_cap': 3.0,              # 总杠杆上限（Chan 建议个人账户 ≤3~4x）

    # ── 数据 ──
    'min_history_days': 252,          # 品种至少需要 1 年历史数据
    'request_delay': 0.3,             # API 请求间隔（秒），避免被封
}

# 品种池 —— 按 Chan 的跨板块分散化原则，覆盖 7 大板块
# 选取标准：持仓量/成交量 Top30 + 每个板块的代表性品种
FUTURES_UNIVERSE = {
    '贵金属':   ['AU', 'AG'],
    '有色金属': ['CU', 'AL', 'ZN', 'NI'],
    '能源':     ['SC', 'FU'],
    '化工':     ['MA', 'TA', 'SA', 'FG', 'RU', 'V'],
    '黑色':     ['RB', 'I', 'JM', 'J', 'HC'],
    '农产品':   ['M', 'RM', 'Y', 'OI', 'P', 'A', 'SR'],
    '新能源':   ['LC', 'SI'],
}
ALL_SYMBOLS = [s for symbols in FUTURES_UNIVERSE.values() for s in symbols]


# ============================================================================
# 第二部分：数据获取层
# ============================================================================

def fetch_futures_daily(symbol, max_retries=3):
    """
    从新浪财经获取期货日线数据。

    API 说明：
      新浪期货日线接口返回 JSONP 格式，包含该合约的完整日线历史。
      实测支持获取 3-5 年的日线数据。

    参数:
        symbol: 合约代码，如 'JM2609'（具体合约）或 'JM0'（连续合约）
    返回:
        list[dict] | [] : 字段 d(日期) o(开) h(高) l(低) c(收) v(量) p(持仓)
    """
    url = (
        f"https://stock2.finance.sina.com.cn/futures/api/jsonp.php/"
        f"var%20day_{symbol}=/InnerFuturesNewService.getDailyKLine?symbol={symbol}"
    )
    for attempt in range(max_retries):
        try:
            req = urllib.request.Request(url)
            req.add_header('User-Agent',
                           'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36')
            data = urllib.request.urlopen(req, timeout=15).read().decode()
            m = re.search(r'\((\[.*\])\)', data)
            if m:
                records = json.loads(m.group(1))
                if records:
                    return records
        except Exception as e:
            if attempt < max_retries - 1:
                time.sleep(1)
            else:
                print(f"  [WARN] {symbol}: {e}")
    return []


def _parse_kline(raw_data):
    """新浪 API 原始数据 → DataFrame"""
    df = pd.DataFrame(raw_data)
    df['date'] = pd.to_datetime(df['d'])
    df = df.rename(columns={
        'o': 'open', 'h': 'high', 'l': 'low',
        'c': 'close', 'v': 'volume', 'p': 'open_interest',
    })
    for col in ['open', 'high', 'low', 'close', 'volume', 'open_interest']:
        df[col] = pd.to_numeric(df[col], errors='coerce')
    df = df.set_index('date').sort_index()
    # 过滤掉价格为 0 的异常数据
    df = df[(df[['open', 'high', 'low', 'close']] > 0).all(axis=1)]
    return df[['open', 'high', 'low', 'close', 'volume', 'open_interest']]


def fetch_continuous_series(symbol_base, years_back=5):
    """
    获取品种的连续价格序列。

    方法 1：尝试新浪连续合约（symbol + '0'，如 JM0）
    方法 2：拼接各月份合约（成交量最大 = 主力）

    这对应 Chan 书中"构建连续期货序列"的最佳实践：
    - 在到期前 roll 到下一主力合约
    - 不做回补调整（动量信号对合约间跳空不敏感）
    """
    print(f"  {symbol_base} ...", end=" ", flush=True)

    # ── 方法 1：连续合约符号 ──
    for suffix in ['0', '']:
        cs = f"{symbol_base}{suffix}"
        raw = fetch_futures_daily(cs)
        if raw and len(raw) >= CONFIG['min_history_days']:
            df = _parse_kline(raw)
            if len(df) >= CONFIG['min_history_days']:
                print(f"OK ({len(df)}d, {df.index[0].date()}~{df.index[-1].date()})")
                return df

    # ── 方法 2：拼接各合约 ──
    print("stitching...", end=" ", flush=True)
    current_year = datetime.now().year
    all_contracts = {}

    for year in range(current_year - years_back, current_year + 1):
        for month in [f'{m:02d}' for m in range(1, 13)]:
            code = f"{symbol_base}{str(year)[-2:]}{month}"
            raw = fetch_futures_daily(code)
            if raw and len(raw) >= 20:
                all_contracts[code] = _parse_kline(raw)
        time.sleep(CONFIG['request_delay'] * 0.5)

    if not all_contracts:
        print("FAIL")
        return pd.DataFrame()

    # 对每个交易日，选成交量最大的合约
    all_dates = sorted(set().union(*[set(df.index) for df in all_contracts.values()]))
    rows = []
    prev_contract = None

    for d in all_dates:
        best = max(
            ((c, df.loc[d, 'volume']) for c, df in all_contracts.items() if d in df.index),
            key=lambda x: x[1], default=(None, 0)
        )
        if best[0]:
            row = all_contracts[best[0]].loc[d].to_dict()
            row['active_contract'] = best[0]
            rows.append(row)
            prev_contract = best[0]

    if not rows:
        print("FAIL")
        return pd.DataFrame()

    df = pd.DataFrame(rows, index=all_dates[:len(rows)])
    print(f"OK ({len(df)}d)")
    return df[['open', 'high', 'low', 'close', 'volume', 'open_interest']]


# ============================================================================
# 第三部分：信号生成层 —— Chan 的 TSMOM 核心公式
# ============================================================================

def compute_momentum_signal(prices, lookback_days):
    """
    时间序列动量信号 —— Chan 书中公式 3.1 的实现。

    signal_t = sign(P_t / P_{t-N} - 1)

    关键理解（Chan 的论述）：
      - 这是"绝对趋势"信号，不是"相对强弱"信号
      - 如果所有品种都在跌，TSMOM 就全线做空（不像横截面动量被迫选相对强的）
      - 这就是为什么 TSMOM 在危机中表现好 —— 危机时一切都在跌，全线做空赚钱

    参数:
        prices: pd.Series 收盘价序列
        lookback_days: 回看天数（12个月 ≈ 252天）
    返回:
        pd.Series: +1（做多）/ -1（做空）/ 0（不持仓）
    """
    ret = prices / prices.shift(lookback_days) - 1
    return np.sign(ret).fillna(0).astype(int)


def compute_vol_ewma(returns, halflife=60):
    """
    波动率估计 —— EWMA 方法（Chan 6.3 节）。

    与简单移动平均的区别：
      EWMA 对近期波动率突变反应更快 —— 这对风险管理至关重要。
      半衰期 60 天意味着：今天的收益率权重 ≈ 60 天前收益率的 2 倍。

    σ_t² = λ·σ_{t-1}² + (1-λ)·r_t²      其中 λ^(halflife) = 0.5

    Chan 为什么不用 GARCH？他在书中直言：对于月度调仓的动量策略，
    EWMA 已经足够；GARCH 增加的复杂度不值得那点边际改进。

    返回:
        pd.Series: 年化波动率估计
    """
    span = halflife / np.log(2)   # EWMA span
    var = returns.pow(2).ewm(span=span, min_periods=halflife).mean()
    return np.sqrt(var * 252)


def generate_signals(price_df, config):
    """
    为单个品种生成完整信号序列。

    Chan 的仓位公式（简化版，来自书中第 6 章）：
      weight_i = signal_i × (σ_target / σ_i,estimated)

    即：波动率越大的品种，仓位越小；波动率越小的品种，仓位越大。
    这样可以确保每个品种对组合的风险贡献大致相等（风险平价）。
    """
    df = price_df[['close']].copy()
    lb = config['primary_lookback'] * 21  # 月 → 交易日

    # 日收益率
    df['daily_ret'] = df['close'].pct_change()

    # TSMOM 信号
    df['signal'] = compute_momentum_signal(df['close'], lb)

    # EWMA 波动率 + 目标波动率缩放
    df['vol_est'] = compute_vol_ewma(df['daily_ret'], config['vol_half_life'])
    df['raw_weight'] = np.where(
        df['vol_est'] > 0,
        df['signal'] * config['target_vol'] / df['vol_est'],
        0.0,
    )
    return df


# ============================================================================
# 第四部分：回测引擎
# ============================================================================

class MomentumBacktest:
    """
    TSMOM 组合回测引擎。

    核心流程（每月重复）：
      1. 对每个品种计算过去 N 月收益率 → 确定方向 (long/short)
      2. 用 EWMA 估计每个品种的波动率
      3. 按 1/σ 分配风险预算 → 高波动品种低仓位，低波动品种高仓位
      4. 施加杠杆上限 → 总敞口不超过 leverage_cap
      5. 执行换仓 → 扣除交易成本
    """

    def __init__(self, config):
        self.config = config
        self.trade_log = []
        self.turnover_log = []
        self.return_series = None
        self.equity_curve = None

    def run(self, price_data):
        """
        主回测循环。

        参数:
            price_data: {symbol: DataFrame(signal, vol_est, raw_weight, daily_ret)}
        """
        config = self.config
        symbols = [s for s, df in price_data.items() if not df.empty]

        # ── 找到所有品种的共同日期范围 ──
        common = None
        for s in symbols:
            valid = set(price_data[s].dropna(subset=['vol_est']).index)
            common = valid if common is None else common & valid
        if not common:
            print("  [ERR] No common trading dates across instruments.")
            return None

        dates = sorted(d for d in common
                       if pd.Timestamp(config['start_date']) <= d <= pd.Timestamp(config['end_date']))
        if len(dates) < 21:
            print(f"  [ERR] Only {len(dates)} trading days — need at least 21.")
            return None

        print(f"  Period : {dates[0].date()} → {dates[-1].date()}")
        print(f"  Days   : {len(dates)}")
        print(f"  Assets : {len(symbols)}")

        # 调仓日
        rebalance_dates = self._rebalance_dates(dates)
        print(f"  Rebal  : {len(rebalance_dates)} ({config['rebalance_freq']})")

        # ── 逐日循环 ──
        weights = {s: 0.0 for s in symbols}         # 当前持仓权重
        daily_rets = []
        capital = config['initial_capital']

        for i, d in enumerate(dates):
            cost = 0.0

            # 调仓日：重新计算目标权重 + 交易成本
            if d in rebalance_dates:
                old_weights = weights.copy()  # 换仓前的权重
                new_weights = self._compute_weights(price_data, symbols, d)
                # 记录交易
                for s in symbols:
                    if new_weights[s] != old_weights[s]:
                        self.trade_log.append({
                            'date': d, 'symbol': s,
                            'old': old_weights[s], 'new': new_weights[s],
                        })
                # 换仓成本（首日无旧持仓，不扣成本）
                if i > 0:
                    to = self._turnover(new_weights, old_weights)
                    cost = to * config['one_way_cost_bps'] / 10000 * 2  # 双边
                    self.turnover_log.append({'date': d, 'turnover': to, 'cost': cost})
                weights = new_weights

            # 计算当日组合收益
            port_ret = 0.0
            active = 0
            for s, w in weights.items():
                if w == 0:
                    continue
                df = price_data[s]
                if d in df.index and pd.notna(df.loc[d, 'daily_ret']):
                    port_ret += w * df.loc[d, 'daily_ret']
                    active += 1

            port_ret -= cost
            daily_rets.append({'date': d, 'return': port_ret, 'n_active': active})
            capital *= (1 + port_ret)

        # ── 存储结果 ──
        self.return_series = pd.Series(
            [r['return'] for r in daily_rets], index=dates
        )
        self.equity_curve = (1 + self.return_series).cumprod()
        return self.return_series

    # ── 内部方法 ──────────────────────────────────────────────

    def _rebalance_dates(self, dates):
        """确定调仓日（默认：每月最后一个交易日）"""
        freq = self.config['rebalance_freq']
        s = pd.Series(range(len(dates)), index=dates)
        freq_map = {
            'daily':   'B',
            'weekly':  'W',
            'monthly': 'ME',
        }
        return set(s.resample(freq_map.get(freq, 'ME')).last().index)

    def _compute_weights(self, price_data, symbols, date):
        """
        组合权重计算 —— Chan 式风险平价。

        步骤：
          1. 从各品种读取 raw_weight（已含 signal × vol_target / vol_est）
          2. 等风险贡献归一化（每个品种除以活跃品种数 N）
          3. 总杠杆约束（Σ|w| ≤ leverage_cap）
        """
        raw = {}
        for s in symbols:
            df = price_data[s]
            if date in df.index:
                w = df.loc[date, 'raw_weight']
                raw[s] = w if pd.notna(w) else 0.0
            else:
                raw[s] = 0.0

        n = sum(1 for w in raw.values() if w != 0)
        weights = {s: (raw[s] / n if n > 0 else 0.0) for s in symbols}

        # 杠杆约束
        total_lev = sum(abs(w) for w in weights.values())
        if total_lev > self.config['leverage_cap']:
            scale = self.config['leverage_cap'] / total_lev
            weights = {s: w * scale for s, w in weights.items()}

        return weights

    def _turnover(self, new_w, old_w):
        """单边换手率"""
        return sum(abs(new_w.get(s, 0) - old_w.get(s, 0)) for s in new_w) / 2

    # ── 绩效指标 ──────────────────────────────────────────────

    def get_metrics(self):
        """计算全套回测绩效指标"""
        r = self.return_series
        if r is None or len(r) == 0:
            return {}

        n_years = len(r) / 252
        total_ret = self.equity_curve.iloc[-1] - 1
        ann_ret = (1 + total_ret) ** (1 / n_years) - 1 if n_years > 0 else 0
        ann_vol = r.std() * np.sqrt(252)
        sharpe = (ann_ret - 0.02) / ann_vol if ann_vol > 0 else 0

        # 最大回撤
        rolling_max = self.equity_curve.expanding().max()
        dd = self.equity_curve / rolling_max - 1
        max_dd = dd.min()
        dd_end = dd.idxmin()
        dd_start = rolling_max.loc[:dd_end].idxmax() if pd.notna(dd_end) else None

        # Calmar
        calmar = ann_ret / abs(max_dd) if max_dd != 0 else 0

        # 胜率 & 盈亏比
        win_rate = (r > 0).mean()
        avg_win = r[r > 0].mean() if (r > 0).any() else 0
        avg_loss = abs(r[r < 0].mean()) if (r < 0).any() else 1e-9
        profit_factor = avg_win / avg_loss

        # 换手率
        avg_to = (np.mean([t['turnover'] for t in self.turnover_log])
                  if self.turnover_log else 0)

        # 滚动 2Y Sharpe（Chan 的退出信号）
        roll_2y = r.rolling(252 * 2).apply(
            lambda x: ((x.mean() * 252 - 0.02) / (x.std() * np.sqrt(252)))
            if x.std() > 0 else 0
        )
        sharpe_below_1 = (roll_2y.dropna() < 1.0).mean() if len(roll_2y.dropna()) > 0 else 0

        return {
            '累计收益率':     f'{total_ret:.2%}',
            '年化收益率':     f'{ann_ret:.2%}',
            '年化波动率':     f'{ann_vol:.2%}',
            'Sharpe Ratio':  f'{sharpe:.3f}',
            'Calmar Ratio':  f'{calmar:.3f}',
            '最大回撤':       f'{max_dd:.2%}',
            '回撤起止':       f'{dd_start.date() if dd_start else "N/A"} → {dd_end.date() if pd.notna(dd_end) else "N/A"}',
            '日胜率':         f'{win_rate:.2%}',
            '盈亏比':         f'{profit_factor:.2f}',
            '平均换手率':     f'{avg_to:.2%}',
            '2Y Sharpe<1.0 占比': f'{sharpe_below_1:.1%}',
            '交易笔数':       len(self.trade_log),
            '活跃品种数':     len(set(t['symbol'] for t in self.trade_log)) if self.trade_log else 0,
        }


# ============================================================================
# 第五部分：多回看窗口对比 —— Chan 的分析方法
# ============================================================================

def compare_lookbacks(price_data, config):
    """
    比较不同回看窗口的绩效。

    Chan 在书中第 3 章系统比较了 1/3/6/12 个月回看窗口，
    发现：
      - 1M：信号噪声大，换手率高，交易成本侵蚀利润
      - 12M：信号最稳定，Sharpe 通常最高
      - 组合多窗口可以进一步分散化（但复杂度增加）
    """
    results = []
    for lookback in config['lookback_months']:
        cfg = config.copy()
        cfg['primary_lookback'] = lookback

        # 重新生成信号
        data = {}
        for sym, df in price_data.items():
            if df.empty:
                continue
            sig = generate_signals(df, cfg)
            data[sym] = sig

        bt = MomentumBacktest(cfg)
        ret = bt.run(data)
        if ret is not None and len(ret) > 0:
            m = bt.get_metrics()
            m['回看期'] = f'{lookback}M'
            results.append(m)

    return results


# ============================================================================
# 第六部分：可视化
# ============================================================================

def plot_results(bt, config):
    """绘制回测结果（6 面板图）"""
    try:
        import matplotlib.pyplot as plt
        import matplotlib.dates as mdates
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

    fig, axes = plt.subplots(3, 2, figsize=(16, 12))
    fig.suptitle(
        f'Time-Series Momentum | Lookback={config["primary_lookback"]}M | '
        f'Sharpe={bt.get_metrics().get("Sharpe Ratio","N/A")}',
        fontsize=14, fontweight='bold',
    )

    # 1. 净值曲线
    ax = axes[0, 0]
    ax.plot(eq.index, eq.values, 'steelblue', lw=1.5, label='TSMOM')
    ax.axhline(y=1, color='gray', ls='--', alpha=0.5)
    ax.set_title('Equity Curve')
    ax.set_ylabel('NAV'); ax.legend(); ax.grid(alpha=0.3)

    # 2. 回撤
    ax = axes[0, 1]
    ax.fill_between(dd.index, 0, dd.values * 100, color='crimson', alpha=0.3)
    ax.plot(dd.index, dd.values * 100, 'darkred', lw=0.5)
    ax.set_title(f'Drawdown (Max: {dd.min():.1%})')
    ax.set_ylabel('%'); ax.grid(alpha=0.3)

    # 3. 日收益分布
    ax = axes[1, 0]
    ax.hist(r * 100, bins=60, color='steelblue', alpha=0.7, edgecolor='white')
    ax.axvline(x=0, color='red', ls='--', alpha=0.5)
    ax.set_title(f'Daily Returns | Skew={r.skew():.2f} Kurt={r.kurtosis():.2f}')
    ax.set_xlabel('%'); ax.grid(alpha=0.3)

    # 4. 滚动 1Y Sharpe
    ax = axes[1, 1]
    roll_sharpe = r.rolling(252).apply(
        lambda x: (x.mean()*252 - 0.02) / (x.std()*np.sqrt(252)) if x.std()>0 else 0
    )
    ax.plot(roll_sharpe.index, roll_sharpe.values, 'forestgreen', lw=1)
    ax.axhline(y=0, color='red', ls='--', alpha=0.5)
    ax.axhline(y=1, color='gray', ls='--', alpha=0.5, label='Chan 退出阈值 (Sharpe=1)')
    ax.set_title('Rolling 1Y Sharpe'); ax.legend(); ax.grid(alpha=0.3)

    # 5. 月度收益热力图
    ax = axes[2, 0]
    monthly = r.groupby([r.index.year, r.index.month]).apply(
        lambda x: (1+x).prod()-1
    ).unstack()
    if not monthly.empty:
        monthly.columns = [f'{int(c):02d}' for c in monthly.columns]
        im = ax.imshow(monthly * 100, cmap='RdYlGn', aspect='auto', vmin=-8, vmax=8)
        ax.set_xticks(range(len(monthly.columns))); ax.set_xticklabels(monthly.columns)
        ax.set_yticks(range(len(monthly.index))); ax.set_yticklabels(monthly.index)
        ax.set_title('Monthly Returns (%)')
        plt.colorbar(im, ax=ax, label='%')

    # 6. 指标面板
    ax = axes[2, 1]
    ax.axis('off')
    for j, (k, v) in enumerate(bt.get_metrics().items()):
        ax.text(0.05, 0.94 - j*0.08, f'{k}:', fontsize=9, fontweight='bold',
                transform=ax.transAxes, va='top')
        ax.text(0.55, 0.94 - j*0.08, str(v), fontsize=9,
                transform=ax.transAxes, va='top', family='monospace')

    plt.tight_layout()
    path = 'momentum_backtest_results.png'
    plt.savefig(path, dpi=150, bbox_inches='tight')
    print(f"\n  Chart saved: {path}")
    plt.show()


# ============================================================================
# 第七部分：主程序
# ============================================================================

def main():
    print("=" * 65)
    print("  期货时间序列动量 (TSMOM) 策略回测")
    print("  Ernie Chan — Algorithmic Trading: Winning Strategies")
    print("=" * 65)

    # ── 1. 获取数据 ──
    print("\n[1/4] Fetching futures data ...")
    price_data = {}
    failed = []

    for sym in ALL_SYMBOLS:
        df = fetch_continuous_series(sym)
        if not df.empty:
            sig_df = generate_signals(df, CONFIG)
            price_data[sym] = sig_df
        else:
            failed.append(sym)
        time.sleep(CONFIG['request_delay'])

    if failed:
        print(f"\n  No data: {', '.join(failed)}")
    print(f"  Instruments ready: {len(price_data)}")

    if len(price_data) < 3:
        print("\n[FAIL] Need at least 3 instruments. Check network / API.")
        return

    # ── 2. 回测 ──
    print(f"\n[2/4] Running backtest (lookback={CONFIG['primary_lookback']}M) ...")
    bt = MomentumBacktest(CONFIG)
    res = bt.run(price_data)

    if res is None or len(res) == 0:
        print("No results. Date range too short?")
        return

    # ── 3. 绩效 ──
    print(f"\n[3/4] Performance Metrics")
    print("-" * 50)
    for k, v in bt.get_metrics().items():
        print(f"  {k:.<28} {v}")

    # 分年统计
    print(f"\n  Annual Breakdown:")
    print(f"  {'Year':<8} {'Return':>9} {'Vol':>8} {'Sharpe':>8} {'Max DD':>9}")
    print(f"  {'-'*45}")
    for yr, grp in res.groupby(res.index.year):
        yr_ret = (1 + grp).prod() - 1
        yr_vol = grp.std() * np.sqrt(252)
        yr_sharpe = (grp.mean()*252 - 0.02) / yr_vol if yr_vol > 0 else 0
        yr_dd = ((1+grp).cumprod() / (1+grp).cumprod().expanding().max() - 1).min()
        print(f"  {yr:<8} {yr_ret:>8.2%} {yr_vol:>8.2%} {yr_sharpe:>8.2f} {yr_dd:>8.2%}")

    # ── 4. 多回看窗口对比 ──
    print(f"\n[4/4] Lookback Comparison (Chan 3.3)")
    print("-" * 50)
    comparisons = compare_lookbacks(price_data, CONFIG)
    if comparisons:
        print(f"  {'Lookback':<10} {'Sharpe':>8} {'Ann Ret':>9} {'Max DD':>9} {'Trades':>7}")
        print(f"  {'-'*48}")
        for c in comparisons:
            print(f"  {c['回看期']:<10} {c['Sharpe Ratio']:>8} "
                  f"{c['年化收益率']:>9} {c['最大回撤']:>9} {c['交易笔数']:>7}")

    # ── 5. 图表 ──
    plot_results(bt, CONFIG)


if __name__ == '__main__':
    main()
