"""
SHFE 期货数据下载脚本

从公开数据源下载上海期货交易所（SHFE）全品种历史日线数据，
存储到 TradingStudio/docs/contracts/ 目录。

数据来源：东方财富（AKShare futures_zh_daily_sina + get_futures_daily）

用法:
    python download_shfe_data.py                    # 下载所有品种主力连续 + 全合约日线
    python download_shfe_data.py --products cu,au   # 只下载指定品种
    python download_shfe_data.py --main-only        # 只下载主力连续合约
    python download_shfe_data.py --year 2026        # 只下载指定年份全合约数据
"""

import akshare as ak
import pandas as pd
import os
import sys
import time
import argparse
from datetime import datetime
from pathlib import Path

# ============================================================
# 配置
# ============================================================

OUTPUT_DIR = Path(r"C:\Works\ClaudeCode\TradingStudio\docs\contracts")

# SHFE 品种及其 Sina 主力合约代码
SHFE_PRODUCTS = {
    # 有色金属
    "CU": {"name": "铜", "code": "CU0", "exchange": "SHFE", "category": "有色金属"},
    "AL": {"name": "铝", "code": "AL0", "exchange": "SHFE", "category": "有色金属"},
    "ZN": {"name": "锌", "code": "ZN0", "exchange": "SHFE", "category": "有色金属"},
    "PB": {"name": "铅", "code": "PB0", "exchange": "SHFE", "category": "有色金属"},
    "NI": {"name": "镍", "code": "NI0", "exchange": "SHFE", "category": "有色金属"},
    "SN": {"name": "锡", "code": "SN0", "exchange": "SHFE", "category": "有色金属"},
    "AO": {"name": "氧化铝", "code": "AO0", "exchange": "SHFE", "category": "有色金属"},
    # 贵金属
    "AU": {"name": "黄金", "code": "AU0", "exchange": "SHFE", "category": "贵金属"},
    "AG": {"name": "白银", "code": "AG0", "exchange": "SHFE", "category": "贵金属"},
    # 黑色金属
    "RB": {"name": "螺纹钢", "code": "RB0", "exchange": "SHFE", "category": "黑色金属"},
    "WR": {"name": "线材", "code": "WR0", "exchange": "SHFE", "category": "黑色金属"},
    "HC": {"name": "热轧卷板", "code": "HC0", "exchange": "SHFE", "category": "黑色金属"},
    "SS": {"name": "不锈钢", "code": "SS0", "exchange": "SHFE", "category": "黑色金属"},
    # 能源化工
    "FU": {"name": "燃料油", "code": "FU0", "exchange": "SHFE", "category": "能源化工"},
    "BU": {"name": "石油沥青", "code": "BU0", "exchange": "SHFE", "category": "能源化工"},
    "RU": {"name": "天然橡胶", "code": "RU0", "exchange": "SHFE", "category": "能源化工"},
    "SP": {"name": "纸浆", "code": "SP0", "exchange": "SHFE", "category": "能源化工"},
    "BR": {"name": "丁二烯胶", "code": "BR0", "exchange": "SHFE", "category": "能源化工"},
    # 国际品种（上期能源）
    "BC": {"name": "铜(BC)", "code": "BC0", "exchange": "INE", "category": "有色金属"},
    "NR": {"name": "20号胶", "code": "NR0", "exchange": "INE", "category": "能源化工"},
    "SC": {"name": "原油", "code": "SC0", "exchange": "INE", "category": "能源化工"},
    "LU": {"name": "低硫燃料油", "code": "LU0", "exchange": "INE", "category": "能源化工"},
}


def download_main_contract(product_id: str, info: dict) -> pd.DataFrame:
    """下载单个品种的主力连续合约历史日线"""
    symbol = info["code"]
    print(f"  下载 {info['name']}({product_id}) 主力连续 {symbol}...", end=" ")
    try:
        df = ak.futures_zh_daily_sina(symbol=symbol)
        df.columns = ["date", "open", "high", "low", "close", "volume", "hold", "settle"]
        df["date"] = pd.to_datetime(df["date"])
        df["variety"] = product_id
        df["variety_name"] = info["name"]
        df["category"] = info["category"]
        df["contract"] = symbol
        print(f"{len(df)} rows, {df['date'].min().date()} ~ {df['date'].max().date()}")
        return df
    except Exception as e:
        print(f"失败: {e}")
        return None


def download_all_contracts_daily(market: str = "SHFE",
                                  start_date: str = None,
                                  end_date: str = None) -> pd.DataFrame:
    """下载指定市场所有合约的日线数据"""
    print(f"\n  下载 {market} 全合约日线数据...", end=" ")
    try:
        kwargs = {"market": market}
        if start_date:
            kwargs["start_date"] = start_date
        if end_date:
            kwargs["end_date"] = end_date
        else:
            kwargs["end_date"] = datetime.now().strftime("%Y%m%d")

        df = ak.get_futures_daily(**kwargs)
        print(f"{len(df)} rows")
        return df
    except Exception as e:
        print(f"失败: {e}")
        return None


def save_data(df: pd.DataFrame, filename: str, subdir: str = ""):
    """保存数据到 CSV"""
    target_dir = OUTPUT_DIR / subdir
    target_dir.mkdir(parents=True, exist_ok=True)
    filepath = target_dir / filename
    df.to_csv(filepath, index=False, encoding="utf-8-sig")
    size_mb = os.path.getsize(filepath) / (1024 * 1024)
    print(f"    已保存: {filepath} ({size_mb:.1f} MB)")


def main():
    parser = argparse.ArgumentParser(description="下载 SHFE 期货数据")
    parser.add_argument("--products", type=str, default="",
                        help="逗号分隔的品种代码，如 cu,au,ag，默认全部")
    parser.add_argument("--main-only", action="store_true",
                        help="只下载主力连续合约")
    parser.add_argument("--all-only", action="store_true",
                        help="只下载全合约日线")
    parser.add_argument("--year", type=str, default="",
                        help="指定年份，如 2026，用于全合约数据")
    parser.add_argument("--update", action="store_true",
                        help="增量更新模式：只下载最近30天数据")
    args = parser.parse_args()

    # 确保输出目录存在
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    # 筛选品种
    if args.products:
        product_ids = [p.strip().upper() for p in args.products.split(",")]
        products = {k: v for k, v in SHFE_PRODUCTS.items() if k in product_ids}
    else:
        products = SHFE_PRODUCTS

    print(f"{"=" * 60}")
    print(f"SHFE 期货数据下载")
    print(f"输出目录: {OUTPUT_DIR}")
    print(f"品种数: {len(products)}")
    print(f"时间: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print(f"{"=" * 60}")

    # ========== 1. 下载主力连续合约 ==========
    if not args.all_only:
        print(f"\n📈 第一步：下载主力连续合约（{len(products)} 个品种）")
        all_main = []
        for pid, info in products.items():
            df = download_main_contract(pid, info)
            if df is not None:
                all_main.append(df)
                # 单个品种单独保存
                save_data(df, f"{pid}_main.csv", "main_contracts")
            time.sleep(0.5)  # 礼貌性延迟

        if all_main:
            # 合并保存
            combined = pd.concat(all_main, ignore_index=True)
            save_data(combined, "all_shfe_main_contracts.csv", "")
            print(f"\n  主力合约总计: {len(combined)} rows, {combined['variety'].nunique()} 品种")

    # ========== 2. 下载全合约日线数据 ==========
    if not args.main_only:
        print(f"\n📊 第二步：下载全合约日线数据")

        # 分市场下载
        for market, market_name in [("SHFE", "上海期货交易所"), ("INE", "上海国际能源交易中心")]:
            # 检查是否有该市场的品种被选中
            market_products = {k: v for k, v in products.items() if v["exchange"] == market}
            if not market_products:
                continue

            print(f"  {market_name} ({market}):")

            if args.year:
                start = f"{args.year}0101"
                end = f"{args.year}1231"
            elif args.update:
                from datetime import timedelta
                start = (datetime.now() - timedelta(days=30)).strftime("%Y%m%d")
                end = datetime.now().strftime("%Y%m%d")
            else:
                start = None
                end = None

            df = download_all_contracts_daily(market=market, start_date=start, end_date=end)
            if df is not None:
                # 按品种分组保存
                for variety in df["variety"].unique():
                    if variety in products:
                        vdf = df[df["variety"] == variety]
                        vdf = vdf.sort_values(["date", "symbol"])
                        save_data(vdf, f"{variety}_all_contracts.csv", "all_contracts")

                # 合并保存
                combined = df.sort_values(["date", "variety", "symbol"])
                save_data(combined, f"all_{market}_contracts.csv", "")
                time.sleep(0.5)

    print(f"\n✅ 完成！数据存放在: {OUTPUT_DIR}")
    print(f"   子目录: main_contracts/ (主力连续), all_contracts/ (全合约)")


if __name__ == "__main__":
    main()
