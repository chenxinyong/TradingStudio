"""
迁移连续合约数据到 bars_history.duckdb
instrument_id = {product}000 (如 v000, rb000)
价格: DOUBLE (×10⁷) → BIGINT
"""
import duckdb, sys, os

PRODUCT = sys.argv[1] if len(sys.argv) > 1 else "v"
SRC = f"data/continuous/{PRODUCT}_continuous.duckdb"
DST = "data/bars_history.duckdb"

if not os.path.exists(SRC):
    print(f"ERROR: {SRC} not found")
    sys.exit(1)

src = duckdb.connect(SRC, read_only=True)
dst = duckdb.connect(DST)

# Check what tables exist
tables = src.execute("SELECT table_name FROM information_schema.tables WHERE table_schema='main'").fetchall()
print(f"Source tables: {[t[0] for t in tables]}")

# Migrate continuous_15min
inst_id = f"{PRODUCT}000"
table = "continuous_15min"

# Check if table exists in source
if table not in [t[0] for t in tables]:
    print(f"ERROR: {table} not in source")
    sys.exit(1)

# Count rows
cnt = src.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]
print(f"Source: {cnt:,} rows in {table}")

# Check for existing data
existing = dst.execute(
    f"SELECT COUNT(*) FROM bars_1min WHERE instrument_id = '{inst_id}'"
).fetchone()[0]
if existing > 0:
    print(f"Deleting {existing:,} existing '{inst_id}' rows...")
    dst.execute(f"DELETE FROM bars_1min WHERE instrument_id = '{inst_id}'")

# Ensure bars_1min table exists (it should, but just in case)
dst.execute("""
    CREATE TABLE IF NOT EXISTS bars_1min (
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

# Remove existing
dst.execute(f"DELETE FROM bars_1min WHERE instrument_id = '{inst_id}'")

# Migrate: use pandas DataFrame for fast append
import pandas as pd
import numpy as np

# Read all data
df = src.execute(f"""
    SELECT trading_day, bar_time, open, high, low, close, volume,
           COALESCE(tick_count, 0) as tc
    FROM {table}
    ORDER BY bar_time
""").df()

# Transform
df['instrument_id'] = inst_id
df['open'] = df['open'].round().astype('int64')
df['high'] = df['high'].round().astype('int64')
df['low'] = df['low'].round().astype('int64')
df['close'] = df['close'].round().astype('int64')
df['volume'] = df['volume'].fillna(0).astype('int64')
df['turnover'] = 0.0
df['open_interest'] = 0.0
df['tick_count'] = df['tc'].fillna(0).astype('int32')
df = df[['instrument_id', 'trading_day', 'bar_time', 'open', 'high', 'low', 'close', 'volume', 'turnover', 'open_interest', 'tick_count']]

# Append to DuckDB
dst.append('bars_1min', df)
total_inserted = len(df)

print(f"Inserted: {total_inserted:,} rows")

# Verify
verify = dst.execute(
    f"SELECT COUNT(*), MIN(bar_time), MAX(bar_time) FROM bars_1min WHERE instrument_id = '{inst_id}'"
).fetchone()
print(f"Verify: {verify[0]:,} rows, {verify[1]} → {verify[2]}")

src.close()
dst.close()
print("Done.")
