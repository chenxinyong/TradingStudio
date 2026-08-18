#!/usr/bin/env python3
"""
日内 5 分钟动量策略 —— DuckDB 真实历史数据回测
================================================
数据源: C:/Works/Datas/bars_history.duckdb
  - bars_5min: 5 分钟 K 线（价格 × 10⁷ 存储，日盘 09:00-15:00）
  - bars_day:  日线 K 线（用于 TSMOM 过滤器）
  - 合约格式: 小写+000 连续合约（rb000, jm000, cu000, ...）

与模拟回测的区别:
  - 使用真实 5 分钟 K 线（非模拟路径）
  - 每次只跑一条路径（因为数据是真实的，无需 Monte Carlo 降噪）
  - 处理真实的日内微观结构（10:15-10:30 休盘、集合竞价等）
"""

import sys
import os
import warnings
from pathlib import Path
from datetime import datetime
import numpy as np
import pandas as pd
import duckdb

# 导入策略核心组件（复用现有逻辑）
sys.path.insert(0, str(Path(__file__).parent))
from intraday_5min_strategy import (
    IntradayConfig, BarPattern, ObservationSummary,
    analyze_observation_window, evaluate_entry_signal,
    PositionManager, CONTRACT_SPECS, compute_metrics, plot_results,
)

warnings.filterwarnings('ignore')

# ============================================================================
# 数据层
# ============================================================================

PRICE_DIVISOR = 10_000_000   # 数据库价格 → 实际价格
DB_PATH = r"C:\Works\Datas\bars_history.duckdb"

# 符号映射：策略用大写 → 数据库用小写+000
def db_symbol(sym):
    return f"{sym.lower()}000"


def load_daily_data(con, instruments):
    """
    从 bars_day 加载日线数据（用于 TSMOM 过滤）。

    返回: {symbol_upper: DataFrame(index=trading_day)}
    """
    ids = "', '".join(db_symbol(s) for s in instruments)
    sql = f"""
        SELECT instrument_id, trading_day,
               open/{PRICE_DIVISOR} AS open,
               high/{PRICE_DIVISOR} AS high,
               low/{PRICE_DIVISOR}  AS low,
               close/{PRICE_DIVISOR} AS close,
               volume, open_interest
        FROM bars_day
        WHERE instrument_id IN ('{ids}')
        ORDER BY instrument_id, trading_day
    """
    df = con.execute(sql).df()
    df['trading_day'] = pd.to_datetime(df['trading_day'])

    result = {}
    for inst_id, grp in df.groupby('instrument_id'):
        sym = inst_id.replace('000', '').upper()
        result[sym] = grp.set_index('trading_day').sort_index()
    return result


def load_5min_day_session(con, instrument, trading_day):
    """
    加载单个品种、单个交易日的日盘 5 分钟 K 线（09:00-15:00）。

    返回: list[dict] — 每根 K 线的 open/high/low/close/volume，
          价格已除以 PRICE_DIVISOR。
    """
    inst_db = db_symbol(instrument)
    sql = f"""
        SELECT bar_time,
               open/{PRICE_DIVISOR}   AS open,
               high/{PRICE_DIVISOR}   AS high,
               low/{PRICE_DIVISOR}    AS low,
               close/{PRICE_DIVISOR}  AS close,
               volume, open_interest
        FROM bars_5min
        WHERE instrument_id = '{inst_db}'
          AND trading_day   = '{trading_day}'
          AND CAST(SUBSTR(bar_time, 12, 5) AS VARCHAR) BETWEEN '09:00' AND '15:00'
        ORDER BY bar_time
    """
    df = con.execute(sql).df()
    if df.empty:
        return []
    # 提取时间字符串 'HH:MM'
    df['time_str'] = df['bar_time'].str[11:16]
    return df.to_dict('records')


def get_trading_days(con, instruments, start_date, end_date):
    """
    获取所有品种的共同交易日列表。
    只保留每个品种都有至少 6 根日盘 K 线的交易日。
    """
    ids = "', '".join(db_symbol(s) for s in instruments)
    sql = f"""
        SELECT trading_day, COUNT(DISTINCT instrument_id) as n_inst,
               COUNT(*) as n_bars
        FROM bars_5min
        WHERE instrument_id IN ('{ids}')
          AND trading_day BETWEEN '{start_date}' AND '{end_date}'
          AND CAST(SUBSTR(bar_time, 12, 5) AS VARCHAR) BETWEEN '09:00' AND '15:00'
        GROUP BY trading_day
        HAVING n_inst = {len(instruments)}
        ORDER BY trading_day
    """
    df = con.execute(sql).df()
    return df['trading_day'].tolist()


# ============================================================================
# 回测引擎（真实数据版）
# ============================================================================

def preload_5min_data(con, instruments, start_date, end_date):
    """
    一次性加载所有品种的 5 分钟日盘数据到内存（避免 N×M 次 SQL 查询）。

    返回: DataFrame，列: instrument_id, trading_day, bar_time, open, high, low, close, volume, time_str
    """
    ids = "', '".join(db_symbol(s) for s in instruments)
    sql = f"""
        SELECT instrument_id, trading_day, bar_time,
               open/{PRICE_DIVISOR}   AS open,
               high/{PRICE_DIVISOR}   AS high,
               low/{PRICE_DIVISOR}    AS low,
               close/{PRICE_DIVISOR}  AS close,
               volume
        FROM bars_5min
        WHERE instrument_id IN ('{ids}')
          AND trading_day BETWEEN '{start_date}' AND '{end_date}'
          AND CAST(SUBSTR(bar_time, 12, 5) AS VARCHAR) BETWEEN '09:00' AND '15:00'
        ORDER BY instrument_id, trading_day, bar_time
    """
    df = con.execute(sql).df()
    df['trading_day'] = pd.to_datetime(df['trading_day'])
    df['time_str'] = df['bar_time'].str[11:16]
    # 映射 instrument_id → 大写简称
    df['symbol'] = df['instrument_id'].str.replace('000', '').str.upper()
    return df


def run_duckdb_backtest(db_path, instruments, config, contract_specs,
                        start_date='2020-01-02', end_date='2026-06-30',
                        verbose=True):
    """
    使用 DuckDB 真实 5 分钟数据运行日内动量策略回测。

    参数:
        db_path: DuckDB 数据库路径
        instruments: 品种列表 ['RB', 'JM', 'CU', ...]
        config: IntradayConfig 策略参数
        contract_specs: {symbol: contract_spec} 合约规格
        start_date, end_date: 回测区间
    返回:
        dict: {results, trades, return_series, equity_curve, final_capital}
    """
    con = duckdb.connect(db_path, read_only=True)

    # ── 1. 加载日线数据 + 计算 TSMOM ──
    if verbose:
        print(f"  Loading daily data ...")
    daily_data = load_daily_data(con, instruments)
    from futures_momentum_backtest import compute_momentum_signal
    for sym, df in daily_data.items():
        df['tsmom'] = compute_momentum_signal(df['close'], config.tsmom_lookback_days)
        df['daily_ret'] = df['close'].pct_change()

    # ── 2. 预加载全部 5 分钟数据到内存 ──
    if verbose:
        print(f"  Preloading 5-min data ({len(instruments)} instruments) ...")
    bars_all = preload_5min_data(con, instruments, start_date, end_date)
    con.close()

    if verbose:
        print(f"  Loaded {len(bars_all):,} 5-min bars")

    # 获取交易日列表
    trading_days = sorted(bars_all['trading_day'].unique())
    if verbose:
        print(f"  Trading days: {len(trading_days)}")

    if len(trading_days) < 21:
        print("  [ERR] Too few trading days.")
        return None

    # ── 3. 按 (日期, 品种) 分组迭代回测 ──
    # 关键优化：不做 per-day × per-symbol 的 boolean mask 查询，
    # 而是预分组后直接迭代每个 (日期, 品种) 的 K 线组。
    trades_by_day = {}  # {date: [(sym, trade_dict), ...]}
    all_trades = []
    reject_counts = {}

    # 分组：每个 (trading_day, symbol) 一个 group
    groups = list(bars_all.groupby(['trading_day', 'symbol']))
    if verbose:
        print(f"  Processing {len(groups):,} instrument-day groups ...")

    for (td, sym), grp in groups:
        if len(grp) < config.observation_bars + 1:
            continue

        spec = contract_specs.get(sym, CONTRACT_SPECS.get('RB', {}))
        daily_df = daily_data.get(sym)

        # 日线 TSMOM 方向
        daily_dir = 0
        if config.use_daily_filter and daily_df is not None and td in daily_df.index:
            tsmom_val = daily_df.loc[td, 'tsmom']
            daily_dir = int(tsmom_val) if pd.notna(tsmom_val) else 0

        bars = grp.to_dict('records')
        obs_bars = bars[:config.observation_bars]
        obs = analyze_observation_window(obs_bars)

        decision = evaluate_entry_signal(obs, daily_dir, config)
        if decision['signal'] == 0:
            for reason in decision.get('reject_reasons', []):
                key = reason.split('(')[0] if '(' in reason else reason
                reject_counts[key] = reject_counts.get(key, 0) + 1
            continue

        # 执行交易
        entry_price = obs.close_price
        direction = decision['signal']
        pos_mgr = PositionManager(config, entry_price, direction, spec)

        remaining = bars[config.observation_bars:]
        exit_price = remaining[-1]['close']
        exit_reason = 'close'

        for j, bar in enumerate(remaining):
            time_str = bar.get('time_str', '')
            if direction == 1:
                check = [bar['open'], bar['low'], bar['high'], bar['close']]
            else:
                check = [bar['open'], bar['high'], bar['low'], bar['close']]
            for price in check:
                should_exit, exit_px, reason = pos_mgr.update(price, time_str)
                if should_exit:
                    exit_price = exit_px
                    exit_reason = reason
                    break
            if exit_reason != 'close':
                break

        trade = {
            'date': td, 'symbol': sym, 'direction': direction,
            'entry': entry_price, 'exit': exit_price,
            'exit_reason': exit_reason, 'pnl': 0.0,  # 后面算
            'score': decision['score'],
            'confidence': decision['confidence'],
        }
        trades_by_day.setdefault(td, []).append((sym, trade))

    # ── 4. 按日期汇总 PnL（考虑了日内多品种同时开仓的资金管理）──
    capital = config.initial_capital
    daily_results = []
    cons_losses = 0
    sorted_dates = sorted(trades_by_day.keys())

    if verbose:
        print(f"  Settling PnL across {len(sorted_dates)} trading days ...")

    for day_idx, td in enumerate(trading_days):
        day_pnl = 0.0
        day_trades = trades_by_day.get(td, [])

        for sym, trade in day_trades:
            spec = contract_specs.get(sym, CONTRACT_SPECS.get('RB', {}))
            entry_price = trade['entry']
            exit_price = trade['exit']
            direction = trade['direction']
            multiplier = spec.get('multiplier', 10)

            # 仓位（用当日开始的 capital）
            stop_dist = config.hard_stop_pct
            risk_amt = capital * config.risk_per_trade_pct
            lots = max(1, int(risk_amt / (stop_dist * entry_price * multiplier)))
            lots = min(lots, 50)

            margin_req = entry_price * multiplier * lots * spec.get('margin', 0.10)
            if margin_req > capital * config.max_position_pct:
                lots = max(1, int(capital * config.max_position_pct /
                                  (entry_price * multiplier * spec.get('margin', 0.10))))

            pnl_per_lot = direction * (exit_price - entry_price) * multiplier
            gross_pnl = pnl_per_lot * lots
            cost = config.commission_per_lot * lots * 2
            slippage = config.slippage_ticks * spec.get('tick', 1.0) * lots * 2
            trade_pnl = gross_pnl - cost - slippage

            trade['pnl'] = trade_pnl
            trade['lots'] = lots
            day_pnl += trade_pnl

        all_trades.extend([t for _, t in day_trades])

        day_ret = day_pnl / capital if capital > 0 else 0

        if day_ret < -config.max_daily_loss_pct:
            day_pnl = -capital * config.max_daily_loss_pct
            day_ret = -config.max_daily_loss_pct

        if day_ret < 0:
            cons_losses += 1
        else:
            cons_losses = 0
        if cons_losses >= config.max_cons_losses:
            day_pnl = 0.0
            day_ret = 0.0

        capital += day_pnl
        daily_results.append({
            'date': pd.Timestamp(td),
            'pnl': day_pnl, 'return': day_ret,
            'capital': capital, 'n_trades': len(day_trades),
        })

        if verbose and (day_idx + 1) % 252 == 0:
            eq = capital / config.initial_capital - 1
            print(f"    Day {day_idx+1}/{len(trading_days)} | "
                  f"NAV={eq:+.2%} | Trades={len(all_trades)}")

    # 打印拒绝原因分布
    if reject_counts:
        print(f"\n  Signal rejection breakdown (top 10):")
        for reason, cnt in sorted(reject_counts.items(), key=lambda x: -x[1])[:10]:
            print(f"    {reason:.<40} {cnt:>6,}")

    results = pd.DataFrame(daily_results).set_index('date')
    trades_df = pd.DataFrame(all_trades) if all_trades else pd.DataFrame()

    return {
        'results': results,
        'trades': trades_df,
        'return_series': results['return'],
        'equity_curve': (1 + results['return']).cumprod(),
        'final_capital': capital,
        'reject_counts': reject_counts,
    }


# ============================================================================
# 单品种 vs 多品种对比
# ============================================================================

def compare_single_vs_portfolio():
    """对比单品种（RB）和多品种组合的表现"""
    config = IntradayConfig(
        use_daily_filter=True,
        risk_per_trade_pct=0.005,
        max_daily_loss_pct=0.02,
    )

    # 品种池
    portfolio_symbols = [
        'RB', 'JM', 'CU', 'AU', 'SC', 'I', 'M', 'MA',
        'AG', 'ZN', 'NI', 'FU', 'TA', 'SA', 'FG', 'RU',
        'V', 'HC', 'J', 'RM',
    ]

    results = {}

    for label, symbols in [
        ('Single (RB)', ['RB']),
        ('Portfolio (8)', ['RB', 'JM', 'CU', 'AU', 'SC', 'I', 'M', 'MA']),
        ('Portfolio (16)',
         ['RB', 'JM', 'CU', 'AU', 'SC', 'I', 'M', 'MA',
          'AG', 'ZN', 'NI', 'FU', 'TA', 'HC', 'J', 'RM']),
    ]:
        print(f"\n{'='*60}")
        print(f"  {label}: {symbols}")
        print(f"{'='*60}")

        result = run_duckdb_backtest(
            DB_PATH, symbols, config, CONTRACT_SPECS,
            start_date='2021-01-01', end_date='2026-06-30',
            verbose=True,
        )

        if result is None:
            continue

        metrics = compute_metrics(result['return_series'], result['trades'])
        results[label] = {'metrics': metrics, 'result': result}

        print(f"\n  --- {label} Metrics ---")
        for k, v in metrics.items():
            if not isinstance(v, dict):
                print(f"    {k:.<28} {v}")

        # 年度统计
        r = result['return_series']
        print(f"\n    {'Year':<8} {'Return':>9} {'Sharpe':>8} {'Max DD':>9} {'Win%':>7}")
        print(f"    {'-'*44}")
        for yr, grp in r.groupby(r.index.year):
            yr_ret = (1 + grp).prod() - 1
            yr_vol = grp.std() * np.sqrt(252)
            yr_sharpe = (grp.mean()*252-0.02)/yr_vol if yr_vol > 0 else 0
            yr_dd = ((1+grp).cumprod()/(1+grp).cumprod().expanding().max()-1).min()
            yr_wr = (grp > 0).mean()
            print(f"    {yr:<8} {yr_ret:>8.2%} {yr_sharpe:>8.2f} {yr_dd:>8.2%} {yr_wr:>7.1%}")

    # 汇总对比表
    print(f"\n{'='*80}")
    print(f"  Portfolio Comparison Summary")
    print(f"{'='*80}")
    print(f"  {'Strategy':<22} {'Sharpe':>8} {'Ann Ret':>9} {'Max DD':>9} "
          f"{'Win%':>7} {'Trades':>7} {'Final NAV':>10}")
    print(f"  {'-'*76}")
    for label, data in results.items():
        m = data['metrics']
        nav = data['result']['final_capital'] / config.initial_capital - 1
        trades = m.get('总交易次数', 'N/A')
        print(f"  {label:<22} {m['Sharpe Ratio']:>8} {m['年化收益率']:>9} "
              f"{m['最大回撤']:>9} {m['日胜率']:>7} {str(trades):>7} {nav:>9.2%}")

    # 图表
    if results:
        best_label = max(results, key=lambda l: float(
            results[l]['metrics']['Sharpe Ratio'].replace('N/A', '-999')))
        plot_results(results[best_label]['result'], config)

    return results


# ============================================================================
# 主程序
# ============================================================================

def main():
    print("=" * 65)
    print("  日内 5 分钟动量策略 —— REAL DATA Backtest")
    print("  Data: C:\\Works\\Datas\\bars_history.duckdb")
    print("=" * 65)

    results = compare_single_vs_portfolio()

    print(f"\n{'='*65}")
    print("  Backtest complete. Data: 2021-2026 real 5-min bars.")
    print(f"{'='*65}")


if __name__ == '__main__':
    main()
