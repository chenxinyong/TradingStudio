"""Merge SA continuous contract into bars_history"""
import duckdb

c = duckdb.connect('data/bars_history.duckdb')
c.execute("ATTACH 'data/SA_continuous.duckdb' AS src (READ_ONLY)")

# Map src cols to dst cols, NULL for missing
col_map = {
    'bars_1min': "instrument_id, trading_day, bar_time, open, high, low, close, volume, NULL as turnover, NULL as open_interest, NULL as tick_count",
    'bars_5min': "instrument_id, bar_time, open, high, low, close, volume, NULL as turnover",
    'bars_15min': "instrument_id, bar_time, open, high, low, close, volume, NULL as turnover",
    'bars_day': "instrument_id, trading_day, bar_time, open, high, low, close, volume",
    'bars_week': "instrument_id, trading_week, bar_time, open, high, low, close, volume",
}

for dst_tbl, select_sql in col_map.items():
    src_tbl = dst_tbl.replace('bars_', 'continuous_')
    cnt = c.execute(f'SELECT COUNT(*) FROM src.{src_tbl}').fetchone()[0]
    c.execute(f"DELETE FROM {dst_tbl} WHERE instrument_id='sa000'")
    final_sql = select_sql.replace('instrument_id', \"'sa000' as instrument_id\", 1)
    c.execute(f"INSERT INTO {dst_tbl} SELECT {final_sql} FROM src.{src_tbl}")
    new = c.execute(f"SELECT COUNT(*) FROM {dst_tbl} WHERE instrument_id='sa000'").fetchone()[0]
    print(f'{dst_tbl}: {cnt} -> {new}')

c.execute('DETACH src')
r = c.execute("SELECT MAX(trading_day) FROM bars_day WHERE instrument_id='sa000'").fetchone()
print(f'\nSA daily last: {r[0]}')
c.close()
