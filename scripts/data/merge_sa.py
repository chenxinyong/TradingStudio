import duckdb
c = duckdb.connect('data/bars_history.duckdb')
c.execute("ATTACH 'data/SA_continuous.duckdb' AS src (READ_ONLY)")

# 1min/5min/15min — same structure
for dst_tbl in ['bars_1min']:
    src_tbl = dst_tbl.replace('bars_', 'continuous_')
    cnt = c.execute(f'SELECT COUNT(*) FROM src.{src_tbl}').fetchone()[0]
    c.execute(f"DELETE FROM {dst_tbl} WHERE instrument_id='sa000'")
    c.execute(f"""INSERT INTO {dst_tbl}(instrument_id, trading_day, bar_time, open, high, low, close, volume, turnover, open_interest, tick_count)
        SELECT 'sa000', trading_day, bar_time, CAST(open AS BIGINT), CAST(high AS BIGINT), CAST(low AS BIGINT), CAST(close AS BIGINT), CAST(volume AS BIGINT), 0, 0, 0 FROM src.{src_tbl}""")
    print(f'{dst_tbl}: {cnt} ok')

for dst_tbl in ['bars_5min','bars_15min']:
    src_tbl = dst_tbl.replace('bars_', 'continuous_')
    cnt = c.execute(f'SELECT COUNT(*) FROM src.{src_tbl}').fetchone()[0]
    c.execute(f"DELETE FROM {dst_tbl} WHERE instrument_id='sa000'")
    c.execute(f"""INSERT INTO {dst_tbl}(instrument_id, trading_day, bar_time, open, high, low, close, volume, turnover, open_interest, tick_count)
        SELECT 'sa000', strftime(bar_time::TIMESTAMP, '%Y-%m-%d') as trading_day, bar_time, CAST(open AS BIGINT), CAST(high AS BIGINT), CAST(low AS BIGINT), CAST(close AS BIGINT), CAST(volume AS BIGINT), 0, 0, 0 FROM src.{src_tbl}""")
    print(f'{dst_tbl}: {cnt} ok')

# Day — different structure (DOUBLE, no bar_time)
cnt = c.execute('SELECT COUNT(*) FROM src.continuous_day').fetchone()[0]
c.execute("DELETE FROM bars_day WHERE instrument_id='sa000'")
c.execute("""INSERT INTO bars_day(instrument_id, trading_day, bar_time, open, high, low, close, volume, turnover, open_interest, tick_count)
    SELECT 'sa000', trading_day, trading_day, CAST(open AS BIGINT), CAST(high AS BIGINT), CAST(low AS BIGINT), CAST(close AS BIGINT), CAST(volume AS BIGINT), 0, 0, 0 FROM src.continuous_day""")
print(f'bars_day: {cnt} ok')

# Week — different structure
cnt = c.execute('SELECT COUNT(*) FROM src.continuous_week').fetchone()[0]
c.execute("DELETE FROM bars_week WHERE instrument_id='sa000'")
c.execute("""INSERT INTO bars_week(instrument_id, trading_day, bar_time, open, high, low, close, volume, turnover, open_interest, tick_count)
    SELECT 'sa000', last_day, first_day, CAST(open AS BIGINT), CAST(high AS BIGINT), CAST(low AS BIGINT), CAST(close AS BIGINT), CAST(volume AS BIGINT), 0, 0, 0 FROM src.continuous_week""")
print(f'bars_week: {cnt} ok')

c.execute('DETACH src')
r = c.execute("SELECT MAX(trading_day) FROM bars_day WHERE instrument_id='sa000'").fetchone()
print(f'\nSA daily last: {r[0]}')
c.close()
