#!/usr/bin/env python3
"""
展期收益率 (Roll Yield / Carry) 因子 —— 横截面回测
====================================================
因子定义（截面 carry，参考 Koijen-Moskowitz-Pedersen-Vrugt 2018 "Carry"）：
  roll_yield_t = (近月价 - 次月价) / 近月价
    > 0  = 现货升水 (backwardation)  → 做多
    < 0  = 现货贴水 (contango)       → 做空

数据源：C:/Works/Datas/bars_history.duckdb → bars_day
  5363 个 instrument_id，其中 xxx000 是连续合约，其余为单个月份合约。
  近月/次月 = 每个品种当日仍在交易、到期日最早的两个合约。

组合构建（月度调仓，横截面）：
  每月最后一个交易日，按 roll_yield 横截面排序，
  做多 carry 最高的 Top-N 品种，做空 carry 最低的 Bottom-N 品种，
  等风险（波动率倒数）加权，持有到下个月调仓。
"""

import re
import sys
import numpy as np
import pandas as pd
import duckdb

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

DB_PATH = r"C:\Works\Datas\bars_history.duckdb"

# ============================================================================
# 配置
# ============================================================================
CONFIG = {
    'start': '2020-03-01',
    'end': '2026-06-30',
    'top_n': 8,               # 多/空各持有的品种数
    'one_way_cost_bps': 8,    # 单边成本（bp，含手续费+滑点，与 TSMOM 回测一致）
    'vol_half_life': 60,      # 波动率 EWMA 半衰期
    'target_vol': 0.15,       # 每品种目标年化波动率
    'leverage_cap': 2.0,      # 总杠杆上限（多空各一边）
}

# ============================================================================
# 一、加载 + 解析合约
# ============================================================================
def load_contracts():
    con = duckdb.connect(DB_PATH, read_only=True)
    df = con.execute(
        "SELECT instrument_id, trading_day, close, volume, open_interest "
        "FROM bars_day WHERE instrument_id NOT LIKE '%000'"
    ).df()
    con.close()

    df['trading_day'] = pd.to_datetime(df['trading_day'])
    m = df['instrument_id'].str.extract(r'^([A-Za-z]{1,3})(\d{3,4})$')
    df['base'] = m[0].str.lower()
    df = df.dropna(subset=['base'])

    # 到期日 = 每个合约最后一个交易日（其所在月即交割月）
    expiry = df.groupby('instrument_id')['trading_day'].max().rename('expiry')
    df = df.merge(expiry, left_on='instrument_id', right_index=True)
    return df


# ============================================================================
# 二、计算每品种每日的 roll yield（近月 vs 次月）
# ============================================================================
def compute_roll_yield(df, method='front'):
    """
    对每个品种 base、每个交易日，取两个合约算 roll_yield = (near - next) / near。

    method:
      'front' — 到期日最早的两个活跃合约（标准学术定义，但中国近月可能不活跃）
      'main'  — 持仓量（OI）最大的两个合约，再按到期日定 near/next（主力 vs 次主力，可交易）

    返回：DataFrame(index=trading_day, columns=base, values=roll_yield)
    """
    series_dict = {}
    for base, g in df.groupby('base'):
        contracts = g['instrument_id'].unique()
        if len(contracts) < 2:
            continue

        exp = g.groupby('instrument_id')['expiry'].first()
        cols_sorted = exp.sort_values().index.tolist()

        close = g.pivot_table(index='trading_day', columns='instrument_id',
                              values='close', aggfunc='last')[cols_sorted]
        dates = close.index.values
        c = close.values  # (n_days, n_contracts) 已按到期日排序
        n_days = c.shape[0]

        near = np.full(n_days, np.nan)
        nxt = np.full(n_days, np.nan)

        if method == 'front':
            for i in range(n_days):
                valid = np.where(~np.isnan(c[i]))[0]
                if len(valid) >= 2:
                    near[i] = c[i, valid[0]]
                    nxt[i] = c[i, valid[1]]
        elif method == 'main':
            oi = g.pivot_table(index='trading_day', columns='instrument_id',
                               values='open_interest', aggfunc='last')[cols_sorted]
            o = oi.values
            for i in range(n_days):
                valid = np.where(~np.isnan(c[i]) & ~np.isnan(o[i]))[0]
                if len(valid) < 2:
                    continue
                # 持仓量最大的两个合约（仍按到期日排序，故下标小=到期早）
                top2 = np.sort(valid[np.argsort(-o[i, valid])[:2]])
                near[i] = c[i, top2[0]]
                nxt[i] = c[i, top2[1]]

        roll = (near - nxt) / near
        series_dict[base] = pd.Series(roll, index=dates)

    return pd.DataFrame(series_dict)


# ============================================================================
# 三、横截面 carry 组合回测
# ============================================================================
def backtest_carry(roll_yield, close_panel, config, invert=False):
    """
    roll_yield: (trading_day × base) 因子值
    close_panel: (trading_day × base) 各品种收盘价（用于算收益）
    invert: False = 标准 carry（多贴水/backwardation，空升水/contango）
            True  = 反向（多升水，空贴水）
    """
    # 收益率面板（用连续合约 '000' 算收益，避免单个合约跳空）
    rets = close_panel.pct_change()

    # 月度调仓日 = 每月最后一个交易日
    month_end = roll_yield.resample('ME').last().index

    dates = [d for d in rets.index
             if pd.Timestamp(config['start']) <= d <= pd.Timestamp(config['end'])]
    dates = pd.DatetimeIndex(dates)

    # 波动率估计（用于等风险加权）
    vol_ewma = rets.pow(2).ewm(span=config['vol_half_life'] / np.log(2),
                               min_periods=config['vol_half_life']).mean()
    vol_ann = np.sqrt(vol_ewma * 252)

    weights = pd.Series(0.0, index=roll_yield.columns)
    daily_rets = []
    trade_log = []

    rebalance_set = set(month_end)
    for i, d in enumerate(dates):
        cost = 0.0
        if d in rebalance_set and d in roll_yield.index:
            ry = roll_yield.loc[d].dropna()
            if len(ry) >= 2 * config['top_n']:
                long_syms = ry.nlargest(config['top_n']).index
                short_syms = ry.nsmallest(config['top_n']).index
                if invert:
                    long_syms, short_syms = short_syms, long_syms

                new_w = {}
                for s in long_syms:
                    v = vol_ann.loc[d, s] if s in vol_ann.columns and pd.notna(vol_ann.loc[d, s]) else 0.15
                    new_w[s] = config['target_vol'] / v / config['top_n']
                for s in short_syms:
                    v = vol_ann.loc[d, s] if s in vol_ann.columns and pd.notna(vol_ann.loc[d, s]) else 0.15
                    new_w[s] = -config['target_vol'] / v / config['top_n']

                # 杠杆约束（多空各一边）
                gross = sum(abs(w) for w in new_w.values())
                if gross > config['leverage_cap']:
                    new_w = {s: w * config['leverage_cap'] / gross for s, w in new_w.items()}

                # 换手成本
                turnover = sum(abs(new_w.get(s, 0.0) - weights.get(s, 0.0)) for s in set(new_w) | set(weights.index))
                cost = turnover * config['one_way_cost_bps'] / 10000 * 2  # 双边
                trade_log.append({'date': d, 'turnover': turnover, 'cost': cost})

                weights = pd.Series(new_w)
            else:
                weights = pd.Series(0.0, index=roll_yield.columns)

        # 当日组合收益
        port_ret = 0.0
        for s, w in weights.items():
            if w == 0 or s not in rets.columns:
                continue
            r = rets.loc[d, s]
            if pd.notna(r):
                port_ret += w * r
        port_ret -= cost
        daily_rets.append(port_ret)

    r = pd.Series(daily_rets, index=dates)
    return r


# ============================================================================
# 四、Rank IC 检验（每月截面：roll_yield vs 未来 1 月收益）
# ============================================================================
def compute_rank_ic(roll_yield, close_panel):
    month_ends = roll_yield.resample('ME').last().index
    month_ends = [d for d in month_ends if d in close_panel.index]
    ics = []
    for i in range(len(month_ends) - 1):
        d = month_ends[i]
        t1 = month_ends[i + 1]
        ry = roll_yield.loc[d]
        fr = close_panel.loc[t1] / close_panel.loc[d] - 1  # 未来 1 月收益
        common = ry.dropna().index.intersection(fr.dropna().index)
        if len(common) < 10:
            continue
        ic = pd.Series(ry[common]).rank().corr(pd.Series(fr[common]).rank())
        ics.append(ic)
    return pd.Series(ics, index=month_ends[1:])


# ============================================================================
# 四、绩效指标
# ============================================================================
def metrics(r):
    eq = (1 + r).cumprod()
    n_years = len(r) / 252
    total = eq.iloc[-1] - 1
    ann = (1 + total) ** (1 / n_years) - 1 if n_years > 0 else 0
    vol = r.std() * np.sqrt(252)
    sharpe = (ann - 0.02) / vol if vol > 0 else 0
    dd = (eq / eq.expanding().max() - 1).min()
    calmar = ann / abs(dd) if dd != 0 else 0
    wr = (r > 0).mean()
    return {
        '累计收益率': total, '年化收益率': ann, '年化波动率': vol,
        'Sharpe': sharpe, 'Calmar': calmar, '最大回撤': dd, '日胜率': wr,
    }


# ============================================================================
# 主程序
# ============================================================================
def main():
    print("=" * 64)
    print("  展期收益率 (Carry) 因子 —— 横截面回测")
    print("=" * 64)

    print("\n[1/3] 加载合约数据 ...")
    df = load_contracts()
    print(f"  合约数: {df['instrument_id'].nunique():,} | 品种数: {df['base'].nunique()}")

    print("\n[2/3] 计算 roll yield ...")
    roll_yield = compute_roll_yield(df, method='front')
    roll_yield_main = compute_roll_yield(df, method='main')
    print(f"  front 面板: {roll_yield.shape[0]} 交易日 × {roll_yield.shape[1]} 品种")
    print(f"  main  面板: {roll_yield_main.shape[0]} 交易日 × {roll_yield_main.shape[1]} 品种")
    print(f"  样本（front 近端）:")
    sample = roll_yield.tail(3).T.dropna()
    if len(sample) > 0:
        print(sample.iloc[:, :].round(4).head(8).to_string())

    # 连续合约收盘价（用于收益计算，避免单合约跳空）
    con = duckdb.connect(DB_PATH, read_only=True)
    cc = con.execute(
        "SELECT instrument_id, trading_day, close FROM bars_day "
        "WHERE instrument_id LIKE '%000'"
    ).df()
    con.close()
    cc['trading_day'] = pd.to_datetime(cc['trading_day'])
    cc['base'] = cc['instrument_id'].str.lower().str.replace('000', '', regex=False)
    close_panel = cc.pivot_table(index='trading_day', columns='base', values='close', aggfunc='last')

    print("\n[3/3] Rank IC 检验 + 回测 ...")
    for method_name, ry_panel in [('front（近月/次月）', roll_yield),
                                   ('main（主力/次主力）', roll_yield_main)]:
        ic = compute_rank_ic(ry_panel, close_panel)
        ic_mean = ic.mean()
        ic_ir = ic.mean() / ic.std() if ic.std() > 0 else 0
        print("\n" + "=" * 60)
        print(f"  方法: {method_name}")
        print("=" * 60)
        print(f"  Rank IC（roll_yield vs 未来1月收益）:")
        print(f"    月度 IC 均值: {ic_mean:+.4f} | IC_IR: {ic_ir:+.2f} | IC>0 占比: {(ic>0).mean():.1%}")

        for label, inv in [('反向 carry (多升水空贴水)', True),
                           ('标准 carry (多贴水空升水)', False)]:
            r = backtest_carry(ry_panel, close_panel, CONFIG, invert=inv)
            m = metrics(r)
            print(f"\n  {label} —— 扣成本后")
            print(f"    年化 {m['年化收益率']:+.2%} | 波动 {m['年化波动率']:.2%} | "
                  f"Sharpe {m['Sharpe']:+.2f} | 回撤 {m['最大回撤']:.2%} | 累计 {m['累计收益率']:+.2%}")

    ic.to_csv('carry_rank_ic.csv')
    print("\n  结果已保存: carry_rank_ic.csv")

    # ── 稳健性检验 ──
    print("\n" + "=" * 60)
    print("  稳健性检验（main 方法，反向 carry：多升水空贴水）")
    print("=" * 60)
    ry = roll_yield_main
    r = backtest_carry(ry, close_panel, CONFIG, invert=True)
    print(f"  {'Year':<6} {'Return':>9} {'Sharpe':>8} {'MaxDD':>9}")
    for yr, g in r.groupby(r.index.year):
        yr_ret = (1 + g).prod() - 1
        yr_vol = g.std() * np.sqrt(252)
        yr_sh = (g.mean() * 252 - 0.02) / yr_vol if yr_vol > 0 else 0
        yr_dd = ((1 + g).cumprod() / (1 + g).cumprod().expanding().max() - 1).min()
        print(f"  {yr:<6} {yr_ret:>8.2%} {yr_sh:>8.2f} {yr_dd:>8.2%}")

    print(f"\n  top_n 敏感性（反向 carry）:")
    for tn in [5, 8, 12]:
        cfg = {**CONFIG, 'top_n': tn}
        rr = backtest_carry(ry, close_panel, cfg, invert=True)
        mm = metrics(rr)
        print(f"    top_n={tn:<3} 年化 {mm['年化收益率']:+.2%}  Sharpe {mm['Sharpe']:+.2f}  回撤 {mm['最大回撤']:.2%}")

    print(f"\n  成本敏感性（反向 carry, top_n=8）:")
    for bps in [0, 4, 8, 15, 25]:
        cfg = {**CONFIG, 'one_way_cost_bps': bps}
        rr = backtest_carry(ry, close_panel, cfg, invert=True)
        mm = metrics(rr)
        print(f"    单边{bps:>2}bp 年化 {mm['年化收益率']:+.2%}  Sharpe {mm['Sharpe']:+.2f}")


if __name__ == '__main__':
    main()
