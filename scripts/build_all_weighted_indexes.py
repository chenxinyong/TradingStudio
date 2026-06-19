"""
批量生成所有品种的成交量加权价格指数表
输出: bars_history.duckdb 中的 bars_{product}_1min 表
"""
import duckdb
import time

DB = "c:/Works/ClaudeCode/TradingStudio/data/bars_history.duckdb"

conn = duckdb.connect(DB)

# 1. 获取所有品种代码
products = conn.execute("""
    SELECT DISTINCT REGEXP_REPLACE(instrument_id, '[0-9].*$', '') as product
    FROM bars_1min WHERE volume > 0
    ORDER BY product
""").fetchall()
products = [p[0] for p in products]
print(f"品种总数: {len(products)}")

# 2. 统计现有的加权指数表
existing = conn.execute("SHOW TABLES").fetchall()
existing = [t[0] for t in existing]
existing_indexes = [t for t in existing if t.startswith("bars_") and t.endswith("_1min") and t != "bars_1min"]
print(f"已有加权指数: {len(existing_indexes)} 个")

# 3. 逐品种建表
created, failed, skipped = 0, 0, 0
results = []

for prod in products:
    table_name = f"bars_{prod}_1min"

    if table_name in existing:
        skipped += 1
        # 验证已有表
        cnt = conn.execute(f"SELECT COUNT(*) FROM {table_name}").fetchone()[0]
        if prod == 'SA':
            print(f"  {prod}: 跳过(已存在) {cnt:,} bars")
        continue

    try:
        t0 = time.time()
        conn.execute(f"""
            CREATE TABLE {table_name} AS
            WITH contract_bars AS (
                SELECT bar_time, instrument_id,
                       open, high, low, close, volume, turnover, open_interest
                FROM bars_1min
                WHERE instrument_id LIKE '{prod}%' AND volume > 0
            ),
            minute_agg AS (
                SELECT
                    bar_time,
                    SUM(open * volume) / NULLIF(SUM(volume), 0) as open,
                    MAX(high) as high,
                    MIN(low) as low,
                    SUM(close * volume) / NULLIF(SUM(volume), 0) as close,
                    SUM(volume) as volume,
                    SUM(turnover) as turnover,
                    SUM(open_interest) as open_interest,
                    COUNT(DISTINCT instrument_id) as active_contracts,
                    FIRST(instrument_id ORDER BY volume DESC) as dominant_contract
                FROM contract_bars
                GROUP BY bar_time
            )
            SELECT
                bar_time,
                bar_time::DATE as trading_day,
                '{prod}' as instrument_id,
                CAST(open AS BIGINT) as open,
                CAST(high AS BIGINT) as high,
                CAST(low AS BIGINT) as low,
                CAST(close AS BIGINT) as close,
                volume,
                turnover,
                open_interest,
                active_contracts,
                dominant_contract,
                0 as tick_count
            FROM minute_agg
            ORDER BY bar_time
        """)

        cnt = conn.execute(f"SELECT COUNT(*) FROM {table_name}").fetchone()[0]
        elapsed = time.time() - t0
        created += 1
        results.append((prod, cnt, elapsed))
        print(f"  {prod:>6}: {cnt:>8,} bars ({elapsed:.1f}s)")

    except Exception as e:
        failed += 1
        print(f"  {prod:>6}: FAILED - {e}")

# 4. 汇总
print(f"\n{'='*50}")
print(f"完成: 新建 {created}, 跳过 {skipped}, 失败 {failed}")
print(f"{'='*50}")

# 5. 排序输出 Top 30
results.sort(key=lambda x: x[1], reverse=True)
print(f"\n{'Product':>6} | {'Bars':>10} | {'生成耗时'}")
print("-" * 30)
for prod, cnt, elapsed in results[:30]:
    print(f"  {prod:>6} | {cnt:>10,} | {elapsed:.1f}s")

# 6. 总表数
all_tables = conn.execute("SHOW TABLES").fetchall()
index_tables = [t[0] for t in all_tables if t[0].startswith("bars_") and t[0].endswith("_1min")]
print(f"\n总表数: {len(all_tables)} (含 {len(index_tables)} 个加权指数表)")

conn.close()
print("\nDone.")
