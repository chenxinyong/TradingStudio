"""
SA 数据准备: DuckDB → 多级别K线 + 主力连续化
输出: data/sa_daily.parquet, sa_30min.parquet, sa_5min.parquet
"""
import duckdb
import pandas as pd
from pathlib import Path

DB = "c:/Works/ClaudeCode/TradingStudio/data/bars_merged.duckdb"
OUT_DIR = Path("c:/Works/ClaudeCode/TradingStudio/data/sa_chanlun")
OUT_DIR.mkdir(exist_ok=True)


def load_sa_bars(freq: str, conn) -> pd.DataFrame:
    """从 DuckDB 提取 SA 的指定频率 K 线 (合成)"""
    if freq == "1min":
        sql = """
            SELECT
                bar_time as dt,
                FIRST(open) as open,
                MAX(high) as high,
                MIN(low) as low,
                LAST(close) as close,
                SUM(volume) as vol,
                SUM(turnover) as amount
            FROM bars_1min
            WHERE instrument_id LIKE 'SA%' AND volume > 0
            GROUP BY bar_time
            ORDER BY bar_time
        """
    elif freq == "5min":
        # bar_time is TEXT, need to cast to TIMESTAMP first
        sql = """
            SELECT
                date_trunc('minute', bar_time::TIMESTAMP) - INTERVAL (EXTRACT(minute FROM bar_time::TIMESTAMP) % 5) MINUTE as dt,
                FIRST(open) as open,
                MAX(high) as high,
                MIN(low) as low,
                LAST(close) as close,
                SUM(volume) as vol,
                SUM(turnover) as amount
            FROM bars_1min
            WHERE instrument_id LIKE 'SA%' AND volume > 0
            GROUP BY dt
            ORDER BY dt
        """
    elif freq == "30min":
        sql = """
            SELECT
                date_trunc('hour', bar_time::TIMESTAMP) + INTERVAL (FLOOR(EXTRACT(minute FROM bar_time::TIMESTAMP) / 30) * 30) MINUTE as dt,
                FIRST(open) as open,
                MAX(high) as high,
                MIN(low) as low,
                LAST(close) as close,
                SUM(volume) as vol,
                SUM(turnover) as amount
            FROM bars_1min
            WHERE instrument_id LIKE 'SA%' AND volume > 0
            GROUP BY dt
            ORDER BY dt
        """
    elif freq == "day":
        sql = """
            SELECT
                bar_time::DATE as dt,
                FIRST(open) as open,
                MAX(high) as high,
                MIN(low) as low,
                LAST(close) as close,
                SUM(volume) as vol,
                SUM(turnover) as amount
            FROM bars_1min
            WHERE instrument_id LIKE 'SA%' AND volume > 0
            GROUP BY bar_time::DATE
            ORDER BY dt
        """
    else:
        raise ValueError(f"Unknown freq: {freq}")

    return conn.execute(sql).df()


def main():
    conn = duckdb.connect(DB)

    for freq, desc in [("day", "日线"), ("30min", "30分钟"), ("5min", "5分钟")]:
        print(f"提取 SA {desc} ({freq})...")
        df = load_sa_bars(freq, conn)
        # DuckDB returns prices as int (x10^7), convert
        for col in ["open", "high", "low", "close"]:
            if col in df.columns:
                df[col] = df[col] / 1e7
        df["dt"] = pd.to_datetime(df["dt"])
        df = df.sort_values("dt").reset_index(drop=True)

        out_file = OUT_DIR / f"sa_{freq}.parquet"
        df.to_parquet(out_file)
        print(f"  → {out_file}: {len(df):,} bars, {df['dt'].min()} ~ {df['dt'].max()}")

        # 基本信息
        print(f"     价格范围: {df['close'].min():.0f} ~ {df['close'].max():.0f}")
        print(f"     日均量: {df['vol'].mean()/1e4:.0f}万 ({df['vol'].sum()/1e8:.1f}亿总)")
        print()

    # 保存合约级别 1min 数据用于主力识别
    print("提取 SA 合约级数据...")
    contracts = conn.execute("""
        SELECT
            instrument_id,
            bar_time::DATE as dt,
            SUM(volume) as day_vol,
            AVG(close)/1e7 as avg_close,
            COUNT(*) as bars
        FROM bars_1min
        WHERE instrument_id LIKE 'SA%' AND volume > 0
        GROUP BY instrument_id, bar_time::DATE
        ORDER BY instrument_id, dt
    """).df()
    contracts.to_parquet(OUT_DIR / "sa_contracts_daily.parquet")
    print(f"  → {OUT_DIR / 'sa_contracts_daily.parquet'}: {len(contracts):,} rows")

    # 主力合约识别 (每日成交量最大的合约)
    print("\n主力合约识别...")
    dominant = contracts.loc[contracts.groupby("dt")["day_vol"].idxmax()].copy()
    dominant = dominant.sort_values("dt").reset_index(drop=True)
    dominant["prev_inst"] = dominant["instrument_id"].shift(1)
    dominant["roll"] = dominant["instrument_id"] != dominant["prev_inst"]

    rolls = dominant[dominant["roll"]]
    print(f"  主力切换: {len(rolls)} 次")
    for _, r in rolls.iterrows():
        print(f"    {r['dt']}: {r['prev_inst']} → {r['instrument_id']}")

    dominant[["dt", "instrument_id", "day_vol", "avg_close"]].to_parquet(
        OUT_DIR / "sa_dominant_contract.parquet"
    )
    print(f"  → {OUT_DIR / 'sa_dominant_contract.parquet'}")

    conn.close()
    print("\n✓ 数据准备完成")


if __name__ == "__main__":
    main()
