"""
Fetch daily market data for trading journal (2026-06-17).
Usage: python scripts/fetch_daily_data.py
"""
import akshare as ak
import json
import sys
from datetime import datetime

TODAY = "2026-06-17"

# === Futures: Major commodity contracts (continuous) ===
FUTURES_SYMBOLS = {
    # 贵金属
    "AU0": "黄金", "AG0": "白银",
    # 有色金属
    "CU0": "铜", "AL0": "铝", "ZN0": "锌", "NI0": "镍", "SN0": "锡", "PB0": "铅",
    # 能源
    "SC0": "原油", "FU0": "燃料油",
    # 化工
    "TA0": "PTA", "MA0": "甲醇", "V0": "PVC", "L0": "LLDPE", "EG0": "乙二醇",
    # 黑色系
    "RB0": "螺纹", "HC0": "热卷", "I0": "铁矿", "JM0": "焦煤", "J0": "焦炭",
    # 农产品
    "M0": "豆粕", "Y0": "豆油", "P0": "棕榈油", "SR0": "白糖", "CF0": "棉花", "RM0": "菜粕",
    # 新能源
    "LC0": "碳酸锂", "SI0": "工业硅",
}

# === Stock Index Futures ===
INDEX_FUTURES = {
    "IF0": "IF(沪深300)", "IC0": "IC(中证500)", "IM0": "IM(中证1000)", "IH0": "IH(上证50)",
}

# === Stock Indices ===
STOCK_INDICES = {
    "sh000001": "上证指数",
    "sz399001": "深证成指",
    "sz399006": "创业板指",
    "sh000688": "科创50",
    "sz399317": "国证A股",
    "sh000016": "上证50",
    "sh000300": "沪深300",
    "sh000905": "中证500",
    "sz399852": "中证1000",
}


def get_futures_data(symbol):
    """Get today and yesterday bar for a futures contract."""
    try:
        df = ak.futures_zh_daily_sina(symbol=symbol)
        if len(df) < 2:
            return None
        today = df[df['date'] == TODAY]
        if len(today) == 0:
            return None
        today_idx = df[df['date'] == TODAY].index[0]
        today_row = df.loc[today_idx]
        prev_row = df.loc[today_idx - 1] if today_idx > 0 else None
        prev2_row = df.loc[today_idx - 2] if today_idx > 1 else None

        prev_close = float(prev_row['close']) if prev_row is not None else None
        prev_hold = float(prev_row['hold']) if prev_row is not None else None

        close = float(today_row['close'])
        hold = float(today_row['hold'])
        volume = float(today_row['volume'])

        chg_pct = ((close - prev_close) / prev_close * 100) if prev_close and prev_close != 0 else None
        hold_chg = hold - prev_hold if prev_hold is not None else None

        return {
            "close": round(close, 3),
            "chg_pct": round(chg_pct, 2) if chg_pct is not None else None,
            "volume": int(volume),
            "hold": int(hold),
            "hold_chg": int(hold_chg) if hold_chg is not None else None,
        }
    except Exception as e:
        return {"error": str(e)}


def get_index_data(symbol):
    """Get today's index close."""
    try:
        df = ak.stock_zh_index_daily(symbol=symbol)
        today = df[df['date'] == TODAY]
        if len(today) == 0:
            return None
        today_row = today.iloc[0]
        today_idx = df[df['date'] == TODAY].index[0]
        prev_row = df.iloc[today_idx - 1] if today_idx > 0 else None

        close = float(today_row['close'])
        prev_close = float(prev_row['close']) if prev_row is not None else None
        chg_pct = ((close - prev_close) / prev_close * 100) if prev_close and prev_close != 0 else None

        return {
            "close": round(close, 2),
            "chg_pct": round(chg_pct, 2) if chg_pct is not None else None,
        }
    except Exception as e:
        return {"error": str(e)}


def main():
    results = {
        "date": TODAY,
        "futures": {},
        "index_futures": {},
        "stock_indices": {},
    }

    print(f"=== Fetching data for {TODAY} ===\n")

    # Commodity futures
    print("--- Commodity Futures ---")
    for sym, name in FUTURES_SYMBOLS.items():
        data = get_futures_data(sym)
        results["futures"][sym] = {"name": name, **data}
        if data and "error" not in data:
            chg = f"{data['chg_pct']:+.2f}%" if data['chg_pct'] is not None else "N/A"
            print(f"  {sym} {name}: close={data['close']} chg={chg} hold_chg={data['hold_chg']}")
        else:
            print(f"  {sym} {name}: ERROR - {data}")
        sys.stdout.flush()

    # Index futures
    print("\n--- Index Futures ---")
    for sym, name in INDEX_FUTURES.items():
        data = get_futures_data(sym)
        results["index_futures"][sym] = {"name": name, **data}
        if data and "error" not in data:
            chg = f"{data['chg_pct']:+.2f}%" if data['chg_pct'] is not None else "N/A"
            print(f"  {sym} {name}: close={data['close']} chg={chg} hold_chg={data['hold_chg']}")
        else:
            print(f"  {sym} {name}: ERROR - {data}")
        sys.stdout.flush()

    # Stock indices
    print("\n--- Stock Indices ---")
    for sym, name in STOCK_INDICES.items():
        data = get_index_data(sym)
        results["stock_indices"][sym] = {"name": name, **data}
        if data and "error" not in data:
            chg = f"{data['chg_pct']:+.2f}%" if data['chg_pct'] is not None else "N/A"
            print(f"  {sym} {name}: close={data['close']} chg={chg}")
        else:
            print(f"  {sym} {name}: ERROR - {data}")
        sys.stdout.flush()

    # Save to JSON
    out_path = "scripts/daily_data_2026-06-17.json"
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(results, f, ensure_ascii=False, indent=2)
    print(f"\nSaved to {out_path}")

    return results


if __name__ == "__main__":
    main()
