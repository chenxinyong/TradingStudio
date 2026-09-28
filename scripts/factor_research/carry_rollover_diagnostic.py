#!/usr/bin/env python3
"""
Carry 换月跳空诊断 — 拼接伪收益有多大？【闸门实验】

背景:
  xxx000 连续合约是「每日取成交量最大合约的 Bar 原样拷贝」
  （src/TradingStudio.Data/Storage/BuildPeriodsService.cs:294，代码注释自述
  「智能拷贝（非聚合）」），**零复权**。换月日 000 从旧合约切到新合约，
  价格跳空幅度 ≈ 月间价差 ≈ 正是 Carry 信号本身。

  若 Carry 策略做多深升水、做空深贴水，两条腿都会收到与 |Carry| 同向的
  伪收益 → 回测 Sharpe 虚高。

方法:
  1. 用 1min 精确匹配识别 000 每日跟踪的合约
     （000 的 1min bar 是被跟踪合约的逐根拷贝，故可精确匹配；已实测 rb 完全吻合）
  2. 在换月日计算跳空分量 = 000 的当日收益 − 被跟踪旧合约的当日收益
  3. 反事实面板: 仅在换月日扣掉跳空分量，其余日与生产完全一致
  4. 同一个 Carry 信号分别跑 Q1−Q5，对比 Sharpe

踩过的坑（勿重蹈）:
  - 用「日线成交量最大」反推主力 → 每天抖动，得出 8418 次换月（真实约 1030 次）。
    生产用 bars_1min 成交量选主力，主力是稳定的，约 3–5 次/年。
  - 用「价格精确匹配」反推主力 → 仅 22% 命中。因为 000 的 bars_day 由 1min 聚合
    （收盘=夜盘 23:00），单月合约 bars_day 来自金数源（收盘=日盘 15:00），两者不同源。

用法:
  python scripts/factor_research/carry_rollover_diagnostic.py
"""

import sys
import warnings

import numpy as np
import pandas as pd
import duckdb

sys.stdout.reconfigure(encoding="utf-8")
warnings.filterwarnings("ignore")

DB = r"C:\Works\Datas\bars_history.duckdb"
PRICE_DIV = 10_000_000.0
IS_END = pd.Timestamp("2023-12-31")
HORIZON = 21  # 与 carry_csmom_factor.py 的月度口径一致

UNIVERSE = [
    "au", "ag", "cu", "al", "zn", "ni", "sn", "pb",
    "sc", "fu", "lu", "bu",
    "ma", "ta", "sa", "fg", "eg", "ur",
    "ru", "v", "l", "pp", "eb",
    "rb", "i", "jm", "j", "hc",
    "m", "rm", "y", "oi", "p", "a",
    "sr", "cf",
    "lc", "si",
    "sp", "ss", "pg", "ec", "ao",
    "br", "nr", "bc",
]


def log(m):
    print(m, file=sys.stderr)


def parse_contract(iid):
    i = 0
    while i < len(iid) and iid[i].isalpha():
        i += 1
    if i == 0 or i == len(iid):
        return "", None
    digits = iid[i:]
    if not digits.isdigit():
        return "", None
    prod = iid[:i].lower()
    if len(digits) == 4:
        return prod, (2000 + int(digits[:2])) * 12 + int(digits[2:])
    if len(digits) == 3:
        return prod, (2020 + int(digits[0])) * 12 + int(digits[1:])
    return prod, None


def main_contract_map(con, prod):
    """用 1min 精确匹配，还原 000 每日跟踪的合约。返回 DataFrame(d, cid)。"""
    q = f"""
     WITH c AS (SELECT trading_day, bar_time, close FROM bars_1min
                WHERE LOWER(instrument_id)='{prod}000'),
          i AS (SELECT instrument_id cid, trading_day, bar_time, close FROM bars_1min
                WHERE LOWER(instrument_id) LIKE '{prod}%'
                  AND LOWER(instrument_id) NOT LIKE '%000')
     SELECT i.cid, CAST(c.trading_day AS DATE) d, COUNT(*) n
     FROM c JOIN i ON c.trading_day=i.trading_day AND c.bar_time=i.bar_time
                 AND c.close=i.close
     GROUP BY 1,2
    """
    r = con.execute(q).df()
    if not len(r):
        return pd.DataFrame(columns=["d", "cid"])
    top = r.sort_values("n", ascending=False).groupby("d").head(1)
    return top[["d", "cid"]].sort_values("d").reset_index(drop=True)


def min1_daily_close(con, prod):
    """该品种全部合约的 1min 派生日收盘（口径统一：当日最后一根 1min 的 close）。
    返回 DataFrame(cid, d, px)。"""
    q = f"""
     SELECT instrument_id cid, CAST(trading_day AS DATE) d,
            arg_max(close, bar_time)/{PRICE_DIV} px
     FROM bars_1min
     WHERE LOWER(instrument_id) LIKE '{prod}%'
       AND LOWER(instrument_id) NOT LIKE '%000'
     GROUP BY 1, 2
    """
    return con.execute(q).df()


def load_day_bars(con):
    """bars_day: 单月合约（信号用）与 000（收益用）。"""
    ind = con.execute("""
        SELECT instrument_id, CAST(trading_day AS DATE) AS tday, close, volume
        FROM bars_day WHERE LOWER(instrument_id) NOT LIKE '%000'
    """).df()
    cont = con.execute("""
        SELECT instrument_id, CAST(trading_day AS DATE) AS tday, close
        FROM bars_day WHERE LOWER(instrument_id) LIKE '%000'
    """).df()

    ind["close"] = ind["close"] / PRICE_DIV
    parsed = ind["instrument_id"].map(parse_contract)
    ind["product"] = [p for p, _ in parsed]
    ind["delivery_ym"] = [y for _, y in parsed]
    ind = ind[ind["product"].isin(UNIVERSE) & ind["delivery_ym"].notna() & (ind["close"] > 0)]

    cont["close"] = cont["close"] / PRICE_DIV
    cont["product"] = cont["instrument_id"].str.lower().str.replace("000", "", regex=False)
    cont = cont[cont["product"].isin(UNIVERSE) & (cont["close"] > 0)]
    return ind, cont.pivot(index="tday", columns="product", values="close").sort_index()


def compute_carry(ind):
    d = ind.copy()
    cur_ym = d["tday"].dt.year * 12 + d["tday"].dt.month
    d = d[d["delivery_ym"] > cur_ym].sort_values(["product", "tday", "delivery_ym"])
    g = d.groupby(["product", "tday"]).head(2)
    cnt = g.groupby(["product", "tday"]).size()
    g = g[g.set_index(["product", "tday"]).index.isin(cnt[cnt >= 2].index)]
    piv = g.groupby(["product", "tday"])["close"].apply(list)
    carry = piv.apply(lambda v: (v[0] - v[1]) / v[1] if len(v) == 2 and v[1] > 0 else np.nan)
    return carry.unstack("product")


def quantile_ls(factor, close_panel, horizon=HORIZON):
    fwd = close_panel.shift(-horizon) / close_panel - 1
    ls = []
    for t, f in factor.iterrows():
        if t not in fwd.index or f.notna().sum() < 10:
            continue
        both = pd.concat([f, fwd.loc[t]], axis=1, keys=["f", "r"]).dropna()
        if len(both) < 10:
            continue
        q = pd.qcut(both["f"].rank(method="first"), 5, labels=False) + 1
        m = both.groupby(q)["r"].mean()
        if 1 in m.index and 5 in m.index:
            ls.append((t, m[1] - m[5]))
    return pd.Series([v for _, v in ls], index=[t for t, _ in ls])


def stats(s, label):
    if len(s) < 5:
        return f"{label}: 样本不足 (n={len(s)})"
    ann = s.mean() * (252 / HORIZON) * 100
    shp = s.mean() / s.std() * np.sqrt(252 / HORIZON) if s.std() > 0 else 0
    t = s.mean() / (s.std() / np.sqrt(len(s))) if s.std() > 0 else 0
    return f"{label}: 年化 {ann:+7.2f}%  Sharpe {shp:+6.3f}  t={t:+5.2f}  n={len(s)}"


def main():
    con = duckdb.connect(DB, read_only=True)
    ind, cont_panel = load_day_bars(con)
    log(f"单月 {len(ind):,} 行；000 面板 {cont_panel.shape}")

    r000 = cont_panel.pct_change().stack()
    r000.index.names = ["day", "product"]

    # ── 逐品种: 识别主力 → 定位换月日 → 计算跳空分量 ──
    log("识别主力合约与换月日 ...")
    jumps = {}       # (product, day) -> jump (000 收益 − 被跟踪合约收益)
    switch_days = []  # (product, day, old_c, new_c)
    for k, prod in enumerate(UNIVERSE, 1):
        try:
            m = main_contract_map(con, prod)
            if len(m) < 2:
                continue
            mc = min1_daily_close(con, prod).set_index(["cid", "d"])["px"].to_dict()
        except Exception as e:
            log(f"  [警告] {prod} 识别失败: {type(e).__name__}: {str(e)[:50]}")
            continue

        m = m.reset_index(drop=True)
        for i in range(1, len(m)):
            old_c, new_c = m.loc[i - 1, "cid"], m.loc[i, "cid"]
            if old_c == new_c:
                continue
            d, d_prev = m.loc[i, "d"], m.loc[i - 1, "d"]
            old_prev = mc.get((old_c, d_prev))
            old_now = mc.get((old_c, d))
            if not old_prev or not old_now or old_prev <= 0:
                continue
            r_held = old_now / old_prev - 1                      # 若继续持有旧合约的收益
            r_actual = r000.get((d, prod))
            if r_actual is None or np.isnan(r_actual):
                continue
            jumps[(prod, d)] = r_actual - r_held                 # 跳空分量
            switch_days.append((prod, d, old_c, new_c, r_actual - r_held))
        if k % 10 == 0:
            log(f"  {k}/{len(UNIVERSE)} ...")

    con.close()

    sw = pd.DataFrame(switch_days, columns=["product", "day", "old_c", "new_c", "jump"])
    all_days = len(UNIVERSE) * len(cont_panel)
    print("═" * 78)
    print("  Carry 换月跳空诊断（闸门实验）")
    print("═" * 78)
    print()
    print("【1】换月事件（1min 精确匹配识别的主力合约）")
    print(f"  46 品种 × {len(cont_panel)} 日 = {all_days:,} 个 (品种,日)")
    print(f"  换月事件 {len(sw):,} 次  ({len(sw)/all_days*100:.2f}% 的交易日)")
    if len(sw):
        per_year = len(sw) / len(UNIVERSE) / (len(cont_panel) / 245)
        print(f"  平均每品种每年 {per_year:.1f} 次")
        j = sw["jump"]
        print(f"  跳空分量: 中位 {j.abs().median()*100:.2f}%  "
              f"p90 {j.abs().quantile(.9)*100:.2f}%  p99 {j.abs().quantile(.99)*100:.2f}%  "
              f"max {j.abs().max()*100:.1f}%")
        print(f"  >2%: {(j.abs()>0.02).sum():,}   >5%: {(j.abs()>0.05).sum():,}")
        print(f"  跳空分量之和（全样本，等权）: {j.sum()*100:+.1f} 个百分点")
        print()
        print("  跳空最大的 6 次:")
        print(sw.reindex(j.abs().sort_values(ascending=False).index)
              .head(6).to_string(index=False))

    # 跳空是否与 Carry 同向
    carry = compute_carry(ind).reindex(cont_panel.index)
    if len(sw):
        c_at = carry.stack().reindex(pd.MultiIndex.from_tuples(
            list(zip(sw["day"], sw["product"])), names=["day", "product"]))
        ok = c_at.notna()
        if ok.sum() > 30:
            corr = np.corrcoef(sw["jump"][ok.values], c_at[ok])[0, 1]
            print()
            print(f"  corr(跳空分量, Carry) = {corr:+.4f}")
            print("  负值 = 低 Carry（深升水）伴随向上跳空 → 正是做多腿的伪收益来源")

    # ── 反事实面板 ──
    r_adj = r000.copy()
    for (prod, d), jv in jumps.items():
        if (d, prod) in r_adj.index:
            r_adj[(d, prod)] = r_adj[(d, prod)] - jv
    r_wide = r_adj.unstack("product").reindex(index=cont_panel.index, columns=cont_panel.columns)

    px = np.full(cont_panel.shape, np.nan)
    for c, col in enumerate(cont_panel.columns):
        s = r_wide[col].values
        base = cont_panel[col].dropna()
        if base.empty:
            continue
        cur = base.iloc[0]
        for i in range(len(s)):
            if not np.isnan(s[i]):
                cur = cur * (1 + s[i])
                px[i, c] = cur
    cf_panel = pd.DataFrame(px, index=cont_panel.index, columns=cont_panel.columns)

    print()
    print("【2】Q1−Q5 多空组合（同一 Carry 信号，仅收益口径不同）")
    ls_raw = quantile_ls(carry, cont_panel)
    ls_cf = quantile_ls(carry, cf_panel)
    print("  " + stats(ls_raw, "A 真实 xxx000"))
    print("  " + stats(ls_cf, "B 剔除跳空  "))
    if len(ls_raw) > 5 and len(ls_cf) > 5:
        common = ls_raw.index.intersection(ls_cf.index)
        is_m = common <= IS_END
        print()
        print("  分段:")
        print("    " + stats(ls_raw[common][is_m], "A IS  ") + "  |  " +
              stats(ls_raw[common][~is_m], "A OOS"))
        print("    " + stats(ls_cf[common][is_m], "B IS  ") + "  |  " +
              stats(ls_cf[common][~is_m], "B OOS"))

    print()
    print("─" * 78)
    print("  判读: B 与 A 接近 → 伪收益不 material，Carry 结论成立")
    print("        B 显著回落 → A 的结论部分来自拼接伪收益，需换口径重做")
    print("─" * 78)


if __name__ == "__main__":
    main()
