#!/usr/bin/env python3
"""
23个重点期货品种 — 全量扫描（自动主力合约）
数据源：akshare futures_zh_daily_sina 连续主力接口（XXX0）
   → 内部按【持仓量】自动切换主力合约，随换月自动更新，无需手工维护具体合约月份。
   → 彻底解决 2026-08-25 发现的"硬编码 2609 合约换月后数据失真"问题。

用法：python scripts/daily/scan_23_contracts.py
"""
import akshare as ak
import sys
from datetime import datetime

# 连续主力代码（XXX0）。akshare 按持仓量自动选主力，换月无需手工改。
CONTRACTS = [
    # (连续代码, 名称, 分类)
    ("AG0", "白银 AG", "贵金属"),
    ("AU0", "黄金 AU", "贵金属"),
    ("CU0", "沪铜 CU", "有色金属"),
    ("SN0", "沪锡 SN", "有色金属"),
    ("NI0", "沪镍 NI", "有色金属"),
    ("AO0", "氧化铝 AO", "有色金属"),
    ("I0",  "铁矿石 I", "黑色系"),
    ("JM0", "焦煤 JM", "黑色系"),
    ("FU0", "燃料油 FU", "能源"),
    ("SA0", "纯碱 SA", "化工"),
    ("FG0", "玻璃 FG", "化工"),
    ("RU0", "天然橡胶 RU", "化工"),
    ("SH0", "烧碱 SH", "化工"),
    ("V0",  "PVC V", "化工"),
    ("UR0", "尿素 UR", "化工"),
    ("TA0", "PTA TA", "化工"),
    ("LC0", "碳酸锂 LC", "新能源"),
    ("SR0", "白糖 SR", "农产品"),
    ("P0",  "棕榈油 P", "农产品"),
    ("CJ0", "红枣 CJ", "农产品"),
    ("CF0", "棉花 CF", "农产品"),
    ("M0",  "豆粕 M", "农产品"),
    ("RM0", "菜粕 RM", "农产品"),
]

CATS_ORDER = ["贵金属", "有色金属", "黑色系", "能源", "化工", "新能源", "农产品"]


def fetch_klines(symbol):
    """拉取连续主力日线，返回 list[dict]，键为英文列名。失败返回 None。"""
    try:
        df = ak.futures_zh_daily_sina(symbol=symbol)
        if df is None or len(df) < 6:
            return None
        return df.to_dict("records")
    except Exception:
        return None


def calc_ma(klines, period):
    closes = [float(k["close"]) for k in klines[-period:]]
    return sum(closes) / len(closes) if closes else 0


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    print("=" * 90)
    print(f"23个重点品种全量扫描（自动主力·持仓量定主力）— {datetime.now().strftime('%Y-%m-%d %H:%M')}")
    print("=" * 90)

    results = []
    for sym, name, cat in CONTRACTS:
        klines = fetch_klines(sym)
        if klines is None:
            results.append({"name": name, "sym": sym, "cat": cat, "error": True})
            continue

        today = klines[-1]
        prev = klines[-2]
        day3 = klines[-3]
        day6 = klines[-6]

        close = float(today["close"])
        prev_close = float(prev["close"])
        chg = (close - prev_close) / prev_close * 100
        oi = int(today["hold"])
        oi_chg = oi - int(prev["hold"])
        chg_3d = (close - float(day3["close"])) / float(day3["close"]) * 100
        chg_5d = (close - float(day6["close"])) / float(day6["close"]) * 100

        ma20 = calc_ma(klines, 20)
        ma20_pct = (close - ma20) / ma20 * 100 if ma20 else 0

        highs = [float(k["high"]) for k in klines[-20:]]
        lows = [float(k["low"]) for k in klines[-20:]]
        h20, l20 = max(highs), min(lows)
        pos_20d = (close - l20) / (h20 - l20) * 100 if h20 != l20 else 50

        vol = int(today["volume"])
        prev_vol = int(prev["volume"])
        vol_ratio = vol / prev_vol if prev_vol else 1

        results.append({
            "name": name, "sym": sym, "cat": cat,
            "close": close, "chg": chg, "oi": oi, "oi_chg": oi_chg,
            "chg_3d": chg_3d, "chg_5d": chg_5d,
            "ma20_pct": ma20_pct, "pos_20d": pos_20d,
            "vol_ratio": vol_ratio,
            "date": today["date"],
        })

    # ============ OUTPUT ============
    print(f"\n{'品种':<14} {'收盘':<10} {'日涨跌':<8} {'3日':<8} {'5日':<8} {'持仓变化':<12} {'MA20位':<9} {'20日位置':<9} {'量能':<9}")
    print("-" * 100)

    for cat in CATS_ORDER:
        cat_results = [r for r in results if r["cat"] == cat]
        if not cat_results:
            continue
        print(f"\n--- {cat} ---")
        for r in cat_results:
            if r.get("error"):
                print(f"  {r['name']:<14} 数据获取失败")
                continue

            if r["oi_chg"] > 5000:
                oi_dir = "大幅增仓"
            elif r["oi_chg"] > 0:
                oi_dir = "增仓"
            elif r["oi_chg"] < -5000:
                oi_dir = "大幅减仓"
            elif r["oi_chg"] < 0:
                oi_dir = "减仓"
            else:
                oi_dir = "—"

            if r["ma20_pct"] > 3:
                ma_pos = f"上方{r['ma20_pct']:.0f}%"
            elif r["ma20_pct"] > 0:
                ma_pos = f"上方{r['ma20_pct']:.0f}%"
            elif r["ma20_pct"] > -3:
                ma_pos = f"附近{r['ma20_pct']:.0f}%"
            else:
                ma_pos = f"下方{abs(r['ma20_pct']):.0f}%"

            if r["vol_ratio"] > 1.5:
                vol_str = f"放量{r['vol_ratio']:.1f}x"
            elif r["vol_ratio"] > 1.2:
                vol_str = f"放量{r['vol_ratio']:.1f}x"
            elif r["vol_ratio"] < 0.7:
                vol_str = f"缩量{r['vol_ratio']:.1f}x"
            else:
                vol_str = "—"

            pos_str = f"{r['pos_20d']:.0f}%" + (" ⚠️高位" if r["pos_20d"] > 80 else (" ⚠️低位" if r["pos_20d"] < 20 else ""))

            print(f"  {r['name']:<14} {r['close']:<10.0f} {r['chg']:+6.2f}% {r['chg_3d']:+6.1f}% {r['chg_5d']:+6.1f}% {r['oi_chg']:>+10,} {ma_pos:<9} {pos_str:<9} {vol_str}")

    # ============ SIGNALS SUMMARY ============
    print("\n" + "=" * 90)
    print("SIGNALS SUMMARY")
    print("=" * 90)

    ok = [r for r in results if not r.get("error")]

    gainers = sorted([r for r in ok if r["chg"] > 2], key=lambda x: -x["chg"])
    print("\n[UP] 大涨 (>2%):", "无" if not gainers else "")
    for r in gainers:
        print(f"  {r['name']}: {r['chg']:+.2f}% | OI {r['oi_chg']:+,} | 20d {r['pos_20d']:.0f}%")

    losers = sorted([r for r in ok if r["chg"] < -1], key=lambda x: x["chg"])
    print("\n[DOWN] 大跌 (<-1%):", "无" if not losers else "")
    for r in losers:
        print(f"  {r['name']}: {r['chg']:+.2f}% | OI {r['oi_chg']:+,} | 20d {r['pos_20d']:.0f}%")

    oi_up = sorted([r for r in ok if r["oi_chg"] > 5000], key=lambda x: -x["oi_chg"])
    print("\n[OI] 持仓大增 (>5000):", "无" if not oi_up else "")
    for r in oi_up:
        tag = "增仓上涨" if r["chg"] > 0 else "增仓下跌"
        print(f"  {r['name']}: OI {r['oi_chg']:+,} | {tag} | {r['chg']:+.2f}%")

    high_pos = [r for r in ok if r["pos_20d"] > 80]
    low_pos = [r for r in ok if r["pos_20d"] < 20]
    if high_pos:
        print("\n[HIGH] 20日高位 (>80%):", ", ".join(f"{r['name']}({r['pos_20d']:.0f}%)" for r in high_pos))
    if low_pos:
        print("\n[LOW] 20日低位 (<20%):", ", ".join(f"{r['name']}({r['pos_20d']:.0f}%)" for r in low_pos))

    trending = [r for r in ok if (r["chg_3d"] > 1 and r["chg_5d"] > 1) or (r["chg_3d"] < -1 and r["chg_5d"] < -1)]
    trending.sort(key=lambda x: -x["chg_5d"])
    print("\n[TREND] 连续趋势 (3日+5日同向):", "无" if not trending else "")
    for r in trending:
        arrow = "UP" if r["chg_5d"] > 0 else "DN"
        print(f"  {r['name']}: 3d {r['chg_3d']:+.1f}% | 5d {r['chg_5d']:+.1f}% {arrow}")

    dates = [r["date"] for r in ok]
    print(f"\nData Date: {dates[0] if dates else 'N/A'} (主力连续，自动换月)")


if __name__ == "__main__":
    main()
