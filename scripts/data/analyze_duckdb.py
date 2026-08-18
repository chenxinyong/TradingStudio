"""DuckDB bars_history 数据分析"""
import duckdb

conn = duckdb.connect("data/bars_history.duckdb", read_only=True)

print("=" * 60)
print("1. TABLES")
print("=" * 60)
tables = conn.execute("SELECT table_name FROM information_schema.tables WHERE table_schema='main'").fetchall()
for t in tables:
    name = t[0]
    cnt = conn.execute(f"SELECT COUNT(*) FROM {name}").fetchone()[0]
    # Get approximate size
    try:
        sz = conn.execute(f"SELECT COUNT(*)*300 FROM {name}").fetchone()[0] / 1024 / 1024
        print(f"  {name:20s}: {cnt:>12,} rows  (~{sz:.0f} MB)")
    except:
        print(f"  {name:20s}: {cnt:>12,} rows")

print()
print("=" * 60)
print("2. TIME COVERAGE (bars_1min)")
print("=" * 60)
r = conn.execute("SELECT MIN(bar_time), MAX(bar_time) FROM bars_1min").fetchone()
print(f"  Range: {r[0]}  ->  {r[1]}")
days = conn.execute("SELECT COUNT(DISTINCT trading_day) FROM bars_1min").fetchone()[0]
print(f"  Trading days: {days:,}")

print()
print("=" * 60)
print("3. INSTRUMENTS")
print("=" * 60)
total_inst = conn.execute("SELECT COUNT(DISTINCT instrument_id) FROM bars_1min").fetchone()[0]
print(f"  Unique instrument_ids: {total_inst:,}")

# Find distinct product prefixes
products = conn.execute("""
    SELECT DISTINCT
        CASE WHEN instrument_id LIKE '__%' AND SUBSTRING(instrument_id,3,1) BETWEEN '0' AND '9'
             THEN SUBSTRING(instrument_id,1,2)
             ELSE SUBSTRING(instrument_id,1,1)
        END as prod
    FROM bars_1min
    ORDER BY 1
""").fetchall()
print(f"  Product codes: {sorted(set(p[0] for p in products))}")

# Check case sensitivity
upper_inst = conn.execute("SELECT COUNT(*) FROM bars_1min WHERE instrument_id != LOWER(instrument_id)").fetchone()[0]
lower_inst = conn.execute("SELECT COUNT(*) FROM bars_1min WHERE instrument_id = LOWER(instrument_id)").fetchone()[0]
print(f"  Lowercase: {lower_inst:,}  Uppercase/Mixed: {upper_inst:,}")

# Samples
samples = conn.execute("SELECT DISTINCT instrument_id FROM bars_1min LIMIT 15").fetchall()
print(f"  Samples: {[s[0] for s in samples]}")

print()
print("=" * 60)
print("4. PER-YEAR BREAKDOWN")
print("=" * 60)
for yr in range(2020, 2027):
    cnt = conn.execute(f"""
        SELECT COUNT(*) FROM bars_1min
        WHERE bar_time >= '{yr}-01-01' AND bar_time < '{yr+1}-01-01'
    """).fetchone()[0]
    inst_cnt = conn.execute(f"""
        SELECT COUNT(DISTINCT instrument_id) FROM bars_1min
        WHERE bar_time >= '{yr}-01-01' AND bar_time < '{yr+1}-01-01'
    """).fetchone()[0]
    if cnt > 0:
        print(f"  {yr}: {cnt:>12,} rows  {inst_cnt:>5} instruments")
    else:
        print(f"  {yr}: (no data)")

print()
print("=" * 60)
print("5. TOP 10 INSTRUMENTS BY ROW COUNT")
print("=" * 60)
top = conn.execute("""
    SELECT instrument_id, COUNT(*) as cnt,
           MIN(bar_time)::VARCHAR as first_bar, MAX(bar_time)::VARCHAR as last_bar,
           MIN(trading_day)::VARCHAR as first_day, MAX(trading_day)::VARCHAR as last_day
    FROM bars_1min
    GROUP BY instrument_id
    ORDER BY cnt DESC
    LIMIT 10
""").fetchall()
for t in top:
    print(f"  {t[0]:8s}: {t[1]:>10,} bars  [{t[4]} -> {t[5]}]")

print()
print("=" * 60)
print("6. DATA QUALITY CHECKS (bars_1min)")
print("=" * 60)

# OHLC inversion
inv = conn.execute("""
    SELECT COUNT(*) FROM bars_1min WHERE high < low OR high < open OR low > close
""").fetchone()[0]
print(f"  OHLC inverted: {inv}")

# Negative values
neg = conn.execute("""
    SELECT COUNT(*) FROM bars_1min WHERE open < 0 OR high < 0 OR low < 0 OR close < 0
""").fetchone()[0]
print(f"  Negative prices: {neg}")

# Zero volume count
zv = conn.execute("""
    SELECT COUNT(*) FROM bars_1min WHERE volume = 0
""").fetchone()[0]
zv_pct = zv / total_inst * 100 if total_inst > 0 else 0
print(f"  Zero volume: {zv:,} ({zv/cnt*100:.1f}% of first year)" if cnt > 0 else f"  Zero volume: {zv:,}")

# Duplicates
dup = conn.execute("""
    SELECT COUNT(*) FROM (
        SELECT instrument_id, bar_time, COUNT(*) as c
        FROM bars_1min GROUP BY instrument_id, bar_time HAVING c > 1
    ) d
""").fetchone()[0]
print(f"  Duplicate (inst+time): {dup}")

# Night session bars
night = conn.execute("""
    SELECT COUNT(*) FROM bars_1min
    WHERE EXTRACT(HOUR FROM bar_time) IN (21,22,23,0,1,2)
""").fetchone()[0]
print(f"  Night session bars: {night:,} ({night/cnt*100:.1f}%)" if cnt > 0 else f"  Night session bars: {night:,}")

# Weekend bars
weekend = conn.execute("""
    SELECT COUNT(*) FROM bars_1min
    WHERE EXTRACT(DOW FROM bar_time) IN (0, 6)
""").fetchone()[0]
print(f"  Weekend bars: {weekend}")

print()
print("=" * 60)
print("7. PRODUCT-LEVEL STATS (for backtest)")
print("=" * 60)

# For each Top30 product, check data availability
top30 = ['AL','ZN','NI','AG','RB','HC','BU','RU','SP','FU',
         'C','M','Y','P','L','V','PP','EB','EG','JM','I',
         'CF','SR','TA','MA','FG','SA','SM','RM','OI']

print(f"  {'Prod':6s} {'Bars':>10s} {'First':>12s} {'Last':>12s} {'Days':>6s}")
print(f"  {'----':6s} {'----':>10s} {'-----':>12s} {'----':>12s} {'----':>6s}")
for prod in top30:
    cnt_p = conn.execute(f"""
        SELECT COUNT(*) FROM bars_1min WHERE instrument_id ILIKE '{prod}%'
    """).fetchone()[0]
    if cnt_p > 0:
        rng = conn.execute(f"""
            SELECT MIN(bar_time)::VARCHAR, MAX(bar_time)::VARCHAR,
                   COUNT(DISTINCT trading_day)
            FROM bars_1min WHERE instrument_id ILIKE '{prod}%'
        """).fetchone()
        first = rng[0][:10] if rng[0] else '?'
        last = rng[1][:10] if rng[1] else '?'
        days = rng[2]
        print(f"  {prod:6s} {cnt_p:>10,} {first:>12s} {last:>12s} {days:>6}")
    else:
        print(f"  {prod:6s} {'NO DATA':>10s}")

conn.close()
print()
print("Done.")
