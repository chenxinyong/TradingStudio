#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Carry + 横截面中期动量 因子研究
================================
数据: bars_history.duckdb (bars_day 表)
  - 连续主力合约 (instrument_id 以 '000' 结尾): 用于横截面动量
  - 单月合约 (带交割月): 用于 Carry 展期收益率

因子定义:
  1. Carry        = (近月收盘 - 次近月收盘) / 次近月收盘   (做多高 carry=贴水)
  2. CSMom_N      = 过去 N 日连续主力收益  (N ∈ 21/63/126)

验证: Rank IC (IS/OOS) + 月度分组回测 (多空)
"""
import re
import numpy as np
import pandas as pd
import duckdb

DB = r"C:\Works\Datas\bars_history.duckdb"
PRICE_DIV = 10_000_000.0

# 流动性品种池 (db 小写码 -> 展示码)，覆盖 7 大板块
UNIVERSE = {
    'au': 'AU', 'ag': 'AG',
    'cu': 'CU', 'al': 'AL', 'zn': 'ZN', 'ni': 'NI', 'sn': 'SN', 'pb': 'PB',
    'sc': 'SC', 'fu': 'FU', 'lu': 'LU', 'bu': 'BU',
    'ma': 'MA', 'ta': 'TA', 'sa': 'SA', 'fg': 'FG', 'eg': 'EG', 'ur': 'UR',
    'ru': 'RU', 'v': 'V', 'l': 'L', 'pp': 'PP', 'eb': 'EB',
    'rb': 'RB', 'i': 'I', 'jm': 'JM', 'j': 'J', 'hc': 'HC',
    'm': 'M', 'rm': 'RM', 'y': 'Y', 'oi': 'OI', 'p': 'P', 'a': 'A',
    'sr': 'SR', 'cf': 'CF',
    'lc': 'LC', 'si': 'SI',
    'sp': 'SP', 'ss': 'SS', 'pg': 'PG', 'ec': 'EC', 'ao': 'AO',
    'br': 'BR', 'nr': 'NR', 'bc': 'BC',
}

IS_END = pd.Timestamp('2023-12-31')


def parse_contract(iid):
    """解析 instrument_id -> (product, year*12+month)。
    - 4 位数字: YYMM (上期所/能源中心/大商所)  e.g. rb2506 -> 2025-06
    - 3 位数字: YMM  (郑商所, Y=年份末位)        e.g. SF503 -> 2025-03
    """
    m = re.match(r'^([A-Za-z]+)(\d+)$', iid)
    if not m:
        return None, None
    prod = m.group(1).lower()
    d = m.group(2)
    if len(d) == 4:
        yy, mm = int(d[:2]), int(d[2:])
        return prod, (2000 + yy) * 12 + mm
    if len(d) == 3:
        y, mm = int(d[0]), int(d[1:])
        return prod, (2020 + y) * 12 + mm
    return prod, None


def load_data():
    con = duckdb.connect(DB, read_only=True)
    cont = con.execute(
        "SELECT instrument_id, trading_day, close FROM bars_day "
        "WHERE LOWER(instrument_id) LIKE '%000'"
    ).df()
    ind = con.execute(
        "SELECT instrument_id, trading_day, close FROM bars_day "
        "WHERE LOWER(instrument_id) NOT LIKE '%000'"
    ).df()
    con.close()

    for df in (cont, ind):
        df['trading_day'] = pd.to_datetime(df['trading_day'])
        df['close'] = df['close'] / PRICE_DIV

    # 连续合约 -> product
    cont['product'] = cont['instrument_id'].str.lower().str.replace('000', '')
    cont = cont[cont['product'].isin(UNIVERSE)]

    # 单月合约 -> (product, delivery_ym)
    meta = {}
    for iid in ind['instrument_id'].unique():
        p, ym = parse_contract(iid)
        if p is not None:
            meta[iid] = (p, ym)
    ind['product'] = ind['instrument_id'].map(lambda x: meta.get(x, (None, None))[0])
    ind['delivery_ym'] = ind['instrument_id'].map(lambda x: meta.get(x, (None, None))[1])
    ind = ind[ind['product'].isin(UNIVERSE) & ind['delivery_ym'].notna()]
    ind['cur_ym'] = ind['trading_day'].dt.year * 12 + ind['trading_day'].dt.month

    return cont, ind


def build_close_panel(cont):
    return cont.pivot_table(index='trading_day', columns='product',
                            values='close').sort_index()


def compute_carry(ind):
    """每 (product, trading_day) 取 delivery_ym > 当前月 的两个最近合约。"""
    # 排除当月及之前交割的合约
    d = ind[ind['delivery_ym'] > ind['cur_ym']].copy()
    d = d.sort_values(['product', 'trading_day', 'delivery_ym'])
    g = d.groupby(['product', 'trading_day'])

    rows = []
    # 向量化分组取前两个
    for (prod, day), grp in g:
        if len(grp) < 2:
            continue
        near, nxt = grp['close'].iloc[0], grp['close'].iloc[1]
        if near > 0 and nxt > 0:
            rows.append({'product': prod, 'trading_day': day,
                         'carry': (near - nxt) / nxt,
                         'near_ym': grp['delivery_ym'].iloc[0],
                         'next_ym': grp['delivery_ym'].iloc[1]})
    if not rows:
        return pd.DataFrame(columns=['product', 'trading_day', 'carry'])
    c = pd.DataFrame(rows)
    return c.pivot_table(index='trading_day', columns='product', values='carry').sort_index()


def momentum_panel(close_panel, lookback):
    return close_panel / close_panel.shift(lookback) - 1


def fwd_return_panel(close_panel, horizon):
    return close_panel.shift(-horizon) / close_panel - 1


def rank_ic_series(factor_panel, fwd_panel):
    """逐日截面 Spearman 相关 -> IC 序列。"""
    ics = []
    for t in factor_panel.index:
        f = factor_panel.loc[t]
        r = fwd_panel.loc[t] if t in fwd_panel.index else None
        if r is None:
            continue
        common = f.dropna().index.intersection(r.dropna().index)
        if len(common) < 10:
            continue
        ic = f.loc[common].corr(r.loc[common], method='spearman')
        if pd.notna(ic):
            ics.append((t, ic))
    if not ics:
        return pd.Series(dtype=float)
    s = pd.Series([ic for _, ic in ics], index=[t for t, _ in ics])
    return s


def ic_stats(ic_series):
    if len(ic_series) == 0:
        return None
    mean = ic_series.mean()
    std = ic_series.std(ddof=1)
    ir = mean / std if std > 0 else np.nan
    pos = (ic_series > 0).mean()
    return {'IC': mean, 'IC_std': std, 'IC_IR': ir, 'IC>0%': pos, 'n': len(ic_series)}


def split_ic(ic_series):
    is_s = ic_series[ic_series.index <= IS_END]
    oos_s = ic_series[ic_series.index > IS_END]
    return ic_stats(is_s), ic_stats(oos_s)


def quantile_backtest(factor_panel, close_panel, horizon=21):
    """月度调仓 5 分组多空。返回 (ls_returns, quintile_annual)"""
    dates = close_panel.index
    ls = []
    quintile_rets = {q: [] for q in range(1, 6)}
    for i in range(0, len(dates) - horizon, horizon):
        t = dates[i]
        t_next = dates[i + horizon]
        if t not in factor_panel.index or t_next not in close_panel.index:
            continue
        f = factor_panel.loc[t].dropna()
        if len(f) < 10:
            continue
        rank = f.rank(method='first')
        q = pd.qcut(rank, 5, labels=False) + 1  # 1..5
        fwd = close_panel.loc[t_next] / close_panel.loc[t] - 1
        fwd = fwd.reindex(f.index)
        grp = fwd.groupby(q).mean()
        for g in range(1, 6):
            quintile_rets[g].append((t, grp.get(g, np.nan)))
        ls.append((t, grp.get(5, np.nan) - grp.get(1, np.nan)))
    ls_s = pd.Series([v for _, v in ls], index=[t for t, _ in ls])
    q_ann = {}
    for g, arr in quintile_rets.items():
        s = pd.Series([v for _, v in arr], index=[t for t, _ in arr]).dropna()
        q_ann[g] = annualize(s) if len(s) else np.nan
    return ls_s, q_ann


def annualize(period_returns):
    """period_returns 是每期(约21日)收益 -> 年化收益 + Sharpe"""
    if len(period_returns) == 0:
        return None
    n_periods = len(period_returns)
    total = (1 + period_returns).prod() - 1
    # 每期约 21 交易日, 年化期数 = 252/21
    periods_per_year = 252 / 21
    ann = (1 + total) ** (periods_per_year / n_periods) - 1
    vol = period_returns.std(ddof=1) * np.sqrt(periods_per_year)
    sharpe = ann / vol if vol > 0 else np.nan
    return {'ann_ret': ann, 'sharpe': sharpe}


def main():
    print("Loading bars_day ...")
    cont, ind = load_data()
    print(f"  continuous rows: {len(cont):,} | individual rows: {len(ind):,}")

    close_panel = build_close_panel(cont)
    close_panel = close_panel.dropna(how='all')
    # 只保留至少 N 个品种有数据的日期
    close_panel = close_panel[close_panel.notna().sum(axis=1) >= 10]
    print(f"  close panel: {close_panel.shape[0]} days × {close_panel.shape[1]} products "
          f"({close_panel.index[0].date()} ~ {close_panel.index[-1].date()})")

    print("Computing carry ...")
    carry_panel = compute_carry(ind)
    carry_panel = carry_panel.reindex(close_panel.index)

    # ---- 因子 + 前向收益 ----
    factors = {}
    factors['Carry'] = carry_panel
    for lb in (21, 63, 126):
        factors[f'CSMom_{lb}d'] = momentum_panel(close_panel, lb)

    horizons = {'1d': 1, '5d': 5, '20d': 20}

    # ---- Rank IC ----
    print("\n" + "=" * 100)
    print("RANK IC 结果  (IS ≤ 2023-12-31 | OOS > 2023-12-31)")
    print("=" * 100)
    ic_report = {}
    for fname, fpanel in factors.items():
        for hname, h in horizons.items():
            fwd = fwd_return_panel(close_panel, h)
            ic = rank_ic_series(fpanel, fwd)
            is_s, oos_s = split_ic(ic)
            ic_report[(fname, hname)] = (is_s, oos_s)

    header = f"{'因子':<14}{'前向':>5} | {'IS IC':>8}{'IS IR':>8}{'IS>0%':>7} | {'OOS IC':>8}{'OOS IR':>8}{'OOS>0%':>7}"
    print(header)
    print("-" * len(header))
    for fname in factors:
        for hname in horizons:
            is_s, oos_s = ic_report[(fname, hname)]
            def fmt(s):
                if s is None:
                    return ("----", "----", "----")
                return (f"{s['IC']:+.3f}", f"{s['IC_IR']:+.2f}", f"{s['IC>0%']:.0%}")
            a, b, c = fmt(is_s)
            d, e, f = fmt(oos_s)
            print(f"{fname:<14}{hname:>5} | {a:>8}{b:>8}{c:>7} | {d:>8}{e:>8}{f:>7}")

    # ---- 月度分组回测 (主因子: Carry, CSMom_63d) ----
    print("\n" + "=" * 100)
    print("月度调仓 5 分组多空  (Q5-Q1)")
    print("=" * 100)
    for fname in ('Carry', 'CSMom_63d', 'CSMom_126d'):
        fpanel = factors[fname]
        ls_s, q_ann = quantile_backtest(fpanel, close_panel, horizon=21)
        if ls_s.empty:
            print(f"  {fname}: 数据不足")
            continue
        is_ls = ls_s[ls_s.index <= IS_END]
        oos_ls = ls_s[ls_s.index > IS_END]
        print(f"\n  [{fname}]")
        print(f"    {'分位':<6}{'年化收益':>12}{'Sharpe':>10}")
        for g in range(1, 6):
            m = q_ann.get(g)
            if m and not np.isnan(m['ann_ret']):
                print(f"    Q{g:<5}{m['ann_ret']:>12.1%}{m['sharpe']:>10.2f}")
        for label, s in (('IS', is_ls), ('OOS', oos_ls)):
            m = annualize(s) if len(s) else None
            if m:
                print(f"    多空(Q5-Q1) {label}: 年化 {m['ann_ret']:+.1%} | Sharpe {m['sharpe']:+.2f} | n={len(s)}")

    # ---- 因子相关性 ----
    print("\n" + "=" * 100)
    print("因子截面相关性 (日均)")
    print("=" * 100)
    fac_keys = list(factors.keys())
    corr = pd.DataFrame(index=fac_keys, columns=fac_keys, dtype=float)
    for a in fac_keys:
        for b in fac_keys:
            if a == b:
                corr.loc[a, b] = 1.0
            else:
                vals = []
                for t in close_panel.index:
                    fa = factors[a].loc[t] if t in factors[a].index else None
                    fb = factors[b].loc[t] if t in factors[b].index else None
                    if fa is None or fb is None:
                        continue
                    common = fa.dropna().index.intersection(fb.dropna().index)
                    if len(common) < 10:
                        continue
                    vals.append(fa.loc[common].corr(fb.loc[common]))
                corr.loc[a, b] = np.mean(vals) if vals else np.nan
    print(corr.to_string(float_format=lambda x: f"{x:+.3f}"))

    print("\nDONE")


if __name__ == '__main__':
    main()
