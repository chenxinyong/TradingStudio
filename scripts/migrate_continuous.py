"""
迁移连续合约所有周期数据到 bars_history.duckdb
instrument_id = {product}000 (如 v000, rb000)
表映射: continuous_1min→bars_1min, 5min→bars_5min, 15min→bars_15min, day→bars_day, week→bars_week
价格: DOUBLE (×10⁷) → BIGINT
"""
import duckdb, sys, os, pandas as pd, numpy as np

PRODUCT = sys.argv[1] if len(sys.argv) > 1 else "v"
SRC = f"data/continuous/{PRODUCT}_continuous.duckdb"
DST = "data/bars_history.duckdb"

if not os.path.exists(SRC):
    print(f"ERROR: {SRC} not found"); sys.exit(1)

src = duckdb.connect(SRC, read_only=True)
dst = duckdb.connect(DST)

inst_id = f"{PRODUCT}000"

# Table mapping: source → destination
TABLE_MAP = {
    "continuous_1min":  "bars_1min",
    "continuous_5min":  "bars_5min",
    "continuous_15min": "bars_15min",
    "continuous_day":   "bars_day",
    "continuous_week":  "bars_week",
}

for src_table, dst_table in TABLE_MAP.items():
    # Check source
    tables = [t[0] for t in src.execute("SELECT table_name FROM information_schema.tables WHERE table_schema='main'").fetchall()]
    if src_table not in tables:
        print(f"SKIP {src_table}: not in source")
        continue

    cnt = src.execute(f"SELECT COUNT(*) FROM {src_table}").fetchone()[0]
    if cnt == 0:
        print(f"SKIP {src_table}: empty"); continue

    # Ensure destination table exists
    dst.execute(f"""
        CREATE TABLE IF NOT EXISTS {dst_table} (
            instrument_id VARCHAR NOT NULL,
            trading_day DATE NOT NULL,
            bar_time TIMESTAMP NOT NULL,
            open BIGINT NOT NULL,
            high BIGINT NOT NULL,
            low BIGINT NOT NULL,
            close BIGINT NOT NULL,
            volume BIGINT NOT NULL,
            turnover DOUBLE NOT NULL,
            open_interest DOUBLE NOT NULL,
            tick_count INTEGER DEFAULT 0,
            PRIMARY KEY (instrument_id, bar_time)
        )
    """)

    # Delete existing
    existing = dst.execute(f"SELECT COUNT(*) FROM {dst_table} WHERE instrument_id='{inst_id}'").fetchone()[0]
    if existing > 0:
        dst.execute(f"DELETE FROM {dst_table} WHERE instrument_id='{inst_id}'")

    # Different tables have different columns — build query dynamically
    src_cols = [c[0] for c in src.execute(f"DESCRIBE {src_table}").fetchall()]
    has_tick = 'tick_count' in src_cols
    has_bar_time = 'bar_time' in src_cols
    has_trading_day = 'trading_day' in src_cols
    has_week_start = 'week_start' in src_cols

    tick_sql = "COALESCE(tick_count, 0)::BIGINT" if has_tick else "0::BIGINT"
    if has_week_start:
        bar_time_sql = "CASE WHEN week_start LIKE '% %' THEN week_start ELSE week_start || ' 00:00:00' END"
        td_sql = "SUBSTRING(week_start,1,10)"
        order_by = "week_start"
    elif has_bar_time:
        bar_time_sql = "bar_time"
        td_sql = "trading_day" if has_trading_day else "SUBSTRING(bar_time,1,10)"
        order_by = "bar_time"
    else:
        bar_time_sql = "trading_day || ' 00:00:00'"
        td_sql = "trading_day"
        order_by = "trading_day"

    df = src.execute(f"""
        SELECT {td_sql} as trading_day, {bar_time_sql} as bar_time,
               open, high, low, close, volume, {tick_sql} as tc
        FROM {src_table} ORDER BY {order_by}
    """).df()

    # Transform
    df['instrument_id'] = inst_id
    for c in ['open','high','low','close']:
        df[c] = df[c].round().astype('int64')
    df['volume'] = df['volume'].fillna(0).astype('int64')
    df['turnover'] = 0.0
    df['open_interest'] = 0.0
    df['tick_count'] = df['tc'].fillna(0).astype('int32')
    df = df[['instrument_id','trading_day','bar_time','open','high','low','close','volume','turnover','open_interest','tick_count']]

    dst.append(dst_table, df)
    print(f"  {src_table} → {dst_table}: {len(df):,} rows")

src.close(); dst.close()
print(f"\nDone. {inst_id} migrated to bars_history.duckdb")

print(f"Inserted: {total_inserted:,} rows")

# Verify
verify = dst.execute(
    f"SELECT COUNT(*), MIN(bar_time), MAX(bar_time) FROM bars_1min WHERE instrument_id = '{inst_id}'"
).fetchone()
print(f"Verify: {verify[0]:,} rows, {verify[1]} → {verify[2]}")

src.close()
dst.close()
print("Done.")
