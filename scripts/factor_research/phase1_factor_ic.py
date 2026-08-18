#!/usr/bin/env python3
"""
Phase 1 — 期货因子计算 + Rank IC 分析
=======================================
从 DuckDB 计算日频因子，评估每个因子的预测能力。

数据源: C:/Works/Datas/bars_history.duckdb
  - bars_1min: 1分钟K线 → 聚合为日频微观结构因子
  - bars_day:  日线K线 → 趋势/波动率因子
  - 价格 × 10⁷ 存储，需除以 PRICE_DIVISOR

因子清单（15个）:
  【微观结构 — 从 bars_1min 聚合】
    rv              已实现波动率（1min对数收益率年化）
    intraday_mom    日内动量（收盘/开盘 - 1）
    intraday_range  日内振幅（(high-low)/open）
    vwap_dev        收盘价相对VWAP偏离
    amihud          Amihud非流动性（|收益|/成交额）
    tick_count      日内成交笔数（对数）

  【趋势/波动率 — 从 bars_day】
    tsmom_20        20日时间序列动量
    tsmom_60        60日时间序列动量
    ma_dev_20       价格/MA20 - 1（趋势偏离）
    hv_20           20日历史波动率
    atr_ratio       ATR(14)/close
    oi_chg_5        5日持仓量变化率
    vol_anom        成交量/20日均量 - 1

评估方法:
  1. Rank IC（Spearman）: 每期截面因子值与未来收益的秩相关
  2. 分组回测: 按因子值分5组，看多空收益差

用法:
  python phase1_factor_ic.py          # 完整运行
  python phase1_factor_ic.py --test   # 小样本测试（3品种×3个月）
"""

import argparse
import warnings
from datetime import datetime

import duckdb
import numpy as np
import pandas as pd

warnings.filterwarnings('ignore')

# ============================================================================
# 配置
# ============================================================================

PRICE_DIVISOR = 10_000_000
DB_PATH = r"C:\Works\Datas\bars_history.duckdb"

START_DATE = '2020-01-01'
END_DATE = '2026-06-30'

# 品种池 —— 7大板块各选活跃连续合约（按成交量排序，覆盖已有品种池）
# 注意：DB 中 instrument_id 大小写混用，这里保留原始大小写
UNIVERSE = {
    '贵金属':   ['au000', 'ag000'],
    '有色金属': ['cu000', 'al000', 'zn000', 'ni000', 'sn000', 'pb000'],
    '能源':     ['sc000', 'fu000', 'bu000'],
    '化工':     ['MA000', 'TA000', 'SA000', 'FG000', 'ru000', 'v000',
                 'pp000', 'l000', 'eg000', 'eb000'],
    '黑色':     ['rb000', 'i000', 'jm000', 'hc000'],
    '农产品':   ['m000', 'p000', 'y000', 'c000', 'a000', 'RM000',
                 'SR000', 'CF000', 'OI000'],
    '新能源':   ['lc000'],
}
ALL_SYMBOLS = [s for syms in UNIVERSE.values() for s in syms]

# 前向收益期（预测目标）
FORWARD_HORIZONS = [1, 5]  # 1天、5天


# ============================================================================
# 数据加载层
# ============================================================================

def load_daily(con, symbols):
    """从 bars_day 加载日线数据 → {symbol: DataFrame}"""
    ids = "', '".join(symbols)
    sql = f"""
        SELECT instrument_id, trading_day,
               open/{PRICE_DIVISOR}  AS open,
               high/{PRICE_DIVISOR}  AS high,
               low/{PRICE_DIVISOR}   AS low,
               close/{PRICE_DIVISOR} AS close,
               volume, turnover, open_interest
        FROM bars_day
        WHERE instrument_id IN ('{ids}')
          AND trading_day BETWEEN '{START_DATE}' AND '{END_DATE}'
        ORDER BY instrument_id, trading_day
    """
    df = con.execute(sql).df()
    df['trading_day'] = pd.to_datetime(df['trading_day'])
    df['symbol'] = df['instrument_id'].str.upper().str.replace('000', '')
    return {s: g.set_index('trading_day').sort_index()
            for s, g in df.groupby('symbol')}


def load_1min_daily_factors(con, symbols):
    """
    从 bars_1min 聚合日频微观结构因子。

    单条 SQL 完成：窗口函数算 1min 对数收益 → 按日聚合。
    返回: DataFrame(index=[instrument_id, trading_day], columns=因子)
    """
    ids = "', '".join(symbols)
    sql = f"""
        WITH bar_rets AS (
            SELECT instrument_id, trading_day, bar_time,
                   open, high, low, close, volume, tick_count,
                   ln(close / lag(close) OVER (
                       PARTITION BY instrument_id, trading_day
                       ORDER BY bar_time)) AS log_ret
            FROM bars_1min
            WHERE instrument_id IN ('{ids}')
              AND trading_day BETWEEN '{START_DATE}' AND '{END_DATE}'
        )
        SELECT instrument_id, trading_day,
               -- 已实现波动率（年化）
               sqrt(sum(log_ret * log_ret) * 252) AS rv,
               -- 日内动量（收盘/开盘 - 1，用对数收益和近似）
               exp(sum(log_ret)) - 1 AS intraday_mom,
               -- 日内振幅
               (max(high) - min(low)) / nullif(first(open ORDER BY bar_time), 0)
                   AS intraday_range,
               -- 收盘价相对VWAP偏离
               (last(close ORDER BY bar_time) -
                sum(close * volume) / nullif(sum(volume), 0))
               / nullif(sum(close * volume) / nullif(sum(volume), 0), 0)
                   AS vwap_dev,
               -- Amihud非流动性（|日内收益| / 日成交额）
               abs(exp(sum(log_ret)) - 1)
               / nullif(sum(volume), 0) AS amihud,
               -- 日内成交笔数（对数）
               ln(sum(tick_count) + 1) AS tick_count
        FROM bar_rets
        GROUP BY instrument_id, trading_day
    """
    df = con.execute(sql).df()
    df['trading_day'] = pd.to_datetime(df['trading_day'])
    df['symbol'] = df['instrument_id'].str.upper().str.replace('000', '')
    return df


# ============================================================================
# 因子计算层（从日线）
# ============================================================================

def compute_daily_factors(daily_data):
    """从日线数据计算趋势/波动率因子，返回面板 DataFrame"""
    frames = []
    for sym, df in daily_data.items():
        d = pd.DataFrame(index=df.index)
        ret = df['close'].pct_change()

        # 时间序列动量
        d['tsmom_20'] = df['close'] / df['close'].shift(20) - 1
        d['tsmom_60'] = df['close'] / df['close'].shift(60) - 1

        # 移动平均偏离
        ma20 = df['close'].rolling(20).mean()
        d['ma_dev_20'] = df['close'] / ma20 - 1

        # 20日历史波动率
        d['hv_20'] = ret.rolling(20).std() * np.sqrt(252)

        # ATR比率
        tr = pd.concat([
            df['high'] - df['low'],
            (df['high'] - df['close'].shift()).abs(),
            (df['low'] - df['close'].shift()).abs(),
        ], axis=1).max(axis=1)
        atr14 = tr.rolling(14).mean()
        d['atr_ratio'] = atr14 / df['close']

        # 5日持仓量变化率
        d['oi_chg_5'] = df['open_interest'] / df['open_interest'].shift(5) - 1

        # 成交量异常（当前量 / 20日均量 - 1）
        d['vol_anom'] = df['volume'] / df['volume'].rolling(20).mean() - 1

        # 前向收益（预测目标）
        # 关键修正：用次日开盘价 open[t+1] 作为入场价，而非 close[t]
        # 原因：vwap_dev / intraday_mom 等因子包含 close[t]，
        #       若收益分母也用 close[t]，会产生机械相关（虚假IC）。
        # 现实含义：close[t] 观察到信号 → open[t+1] 下单 → close[t+h] 平仓。
        for h in FORWARD_HORIZONS:
            # 干净版：次日开盘 → 第h日收盘（可交易，无共享价格）
            d[f'fwd_ret_{h}d'] = df['close'].shift(-h) / df['open'].shift(-1) - 1
            # 对照版：收盘 → 收盘（有前视偏差，仅用于对比）
            d[f'fwd_ret_cc_{h}d'] = df['close'].shift(-h) / df['close'] - 1

        d['symbol'] = sym
        frames.append(d)

    panel = pd.concat(frames)
    panel = panel.reset_index().rename(columns={'trading_day': 'date'})
    return panel.set_index(['date', 'symbol'])


# ============================================================================
# 因子预处理
# ============================================================================

def winsorize_series(s):
    """MAD法去极值（3倍MAD）"""
    med = s.median()
    mad = (s - med).abs().median()
    if mad == 0 or np.isnan(mad):
        return s
    return s.clip(med - 3 * mad, med + 3 * mad)


def cross_sectional_rank(s):
    """截面内百分位排名（Rank标准化，0-1）"""
    return s.groupby(level='date').rank(pct=True)


# ============================================================================
# IC 分析
# ============================================================================

def compute_rank_ic(panel, factor, fwd_ret_col):
    """
    Rank IC = 每期截面内 factor 与 未来收益 的 Spearman 相关。

    返回 IC 序列（按日期）及其统计量。
    """
    df = panel[[factor, fwd_ret_col]].dropna()
    if df.empty:
        return None, {}

    ic_series = df.groupby(level='date').apply(
        lambda g: g[factor].corr(g[fwd_ret_col], method='spearman')
        if g[factor].nunique() > 3 else np.nan
    ).dropna()

    if len(ic_series) == 0:
        return None, {}

    stats = {
        'IC均值': ic_series.mean(),
        'IC标准差': ic_series.std(),
        'IC_IR': ic_series.mean() / ic_series.std() if ic_series.std() > 0 else 0,
        'IC胜率': (ic_series > 0).mean(),
        'IC_t值': ic_series.mean() / (ic_series.std() / np.sqrt(len(ic_series)))
        if ic_series.std() > 0 else 0,
        '样本期数': len(ic_series),
    }
    return ic_series, stats


# ============================================================================
# 分组回测（Quantile Portfolio）
# ============================================================================

def quantile_backtest(panel, factor, fwd_ret_col, n_groups=5):
    """
    每期按因子值分 n_groups 组，计算各组平均前向收益。
    多空组合 = 第1组(因子值最高) - 最后组(因子值最低)。
    """
    df = panel[[factor, fwd_ret_col]].dropna()
    if df.empty:
        return None

    # 每期截面分组
    df['group'] = df.groupby(level='date')[factor].transform(
        lambda x: pd.qcut(x.rank(method='first'), n_groups, labels=False)
        if x.nunique() >= n_groups else np.nan
    )

    # 各组前向收益均值
    group_rets = df.groupby('group')[fwd_ret_col].mean()

    if group_rets.empty or len(group_rets) < n_groups:
        return None

    # 多空组合
    long_ret = group_rets.iloc[0] if group_rets.index.max() < group_rets.index.min() else None
    # 注意 qcut labels=False 时 0=最低组, n-1=最高组
    top = group_rets.get(n_groups - 1, np.nan)  # 因子值最高组
    bottom = group_rets.get(0, np.nan)          # 因子值最低组
    spread = top - bottom

    return {
        'group_rets': group_rets,
        'top_group': top,
        'bottom_group': bottom,
        'long_short_spread': spread,
    }


# ============================================================================
# 主程序
# ============================================================================

def main(test_mode=False):
    global START_DATE, END_DATE

    print("=" * 70)
    print("  Phase 1 — 期货因子计算 + Rank IC 分析")
    print(f"  数据: {DB_PATH}")
    print(f"  区间: {START_DATE} → {END_DATE}")
    print("=" * 70)

    # 测试模式：小样本
    if test_mode:
        test_symbols = ['rb000', 'au000', 'm000']
        test_start, test_end = '2023-01-01', '2023-03-31'
        print(f"\n  [TEST MODE] 品种={test_symbols}, 区间={test_start}→{test_end}")
        START_DATE, END_DATE = test_start, test_end
        symbols = test_symbols
    else:
        symbols = ALL_SYMBOLS

    # ── 1. 加载数据 ──
    print(f"\n[1/4] 加载日线数据 ({len(symbols)} 品种) ...")
    con = duckdb.connect(DB_PATH, read_only=True)
    daily_data = load_daily(con, symbols)
    print(f"  日线品种数: {len(daily_data)}")

    print(f"\n[2/4] 从 bars_1min 聚合微观结构因子 ...")
    micro = load_1min_daily_factors(con, symbols)
    con.close()
    print(f"  微观结构因子行数: {len(micro):,}")

    # ── 2. 计算日线因子 ──
    print(f"\n[3/4] 计算趋势/波动率因子 ...")
    panel_daily = compute_daily_factors(daily_data)

    # 合并微观结构因子
    micro_panel = micro.set_index(['trading_day', 'symbol'])
    micro_panel.index = micro_panel.index.rename(['date', 'symbol'])
    panel = panel_daily.join(micro_panel, how='left')

    print(f"  面板形状: {panel.shape} (日期×品种 × 因子)")
    print(f"  品种数: {panel.index.get_level_values('symbol').nunique()}")
    print(f"  交易日数: {panel.index.get_level_values('date').nunique()}")

    # ── 3. 因子预处理 ──
    factor_cols = ['rv', 'intraday_mom', 'intraday_range', 'vwap_dev',
                   'amihud', 'tick_count',
                   'tsmom_20', 'tsmom_60', 'ma_dev_20', 'hv_20',
                   'atr_ratio', 'oi_chg_5', 'vol_anom']

    # 去极值（时序上做，对每个因子）
    for f in factor_cols:
        if f in panel.columns:
            panel[f] = panel.groupby(level='symbol')[f].transform(winsorize_series)

    # ── 4. IC 分析 ──
    print(f"\n[4/4] Rank IC 分析 (前向收益 1 天) ...")
    print("-" * 70)
    print(f"  {'因子':<18} {'IC均值':>8} {'IC_IR':>7} {'IC胜率':>7} "
          f"{'t值':>6} {'多空价差':>8}")
    print("-" * 70)

    results = []
    for f in factor_cols:
        if f not in panel.columns:
            continue
        ic_series, stats = compute_rank_ic(panel, f, 'fwd_ret_1d')
        if stats:
            qb = quantile_backtest(panel, f, 'fwd_ret_1d')
            spread = qb['long_short_spread'] if qb else np.nan
            results.append({'因子': f, **stats, '多空价差': spread})
            print(f"  {f:<18} {stats['IC均值']:>8.4f} {stats['IC_IR']:>7.3f} "
                  f"{stats['IC胜率']:>7.1%} {stats['IC_t值']:>6.2f} "
                  f"{spread:>8.4f}")

    # ── 5. 5日前向收益的 IC（稳健性检查）──
    print(f"\n  前向收益 5 天的 IC（稳健性对照）:")
    print("-" * 70)
    print(f"  {'因子':<18} {'IC均值':>8} {'IC_IR':>7} {'IC胜率':>7}")
    print("-" * 70)
    for f in factor_cols:
        if f not in panel.columns:
            continue
        ic5, stats5 = compute_rank_ic(panel, f, 'fwd_ret_5d')
        if stats5:
            print(f"  {f:<18} {stats5['IC均值']:>8.4f} {stats5['IC_IR']:>7.3f} "
                  f"{stats5['IC胜率']:>7.1%}")

    # ── 6. 结果汇总到 CSV ──
    if results:
        result_df = pd.DataFrame(results)
        out_path = 'factor_ic_results.csv'
        result_df.to_csv(out_path, index=False, encoding='utf-8-sig')
        print(f"\n  结果已保存: {out_path}")

    print(f"\n{'=' * 70}")
    print("  完成。IC均值>0.03 且 IC_IR>0.3 的因子值得进一步研究。")
    print(f"{'=' * 70}")


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--test', action='store_true', help='小样本测试模式')
    args = parser.parse_args()
    main(test_mode=args.test)
