import duckdb
c = duckdb.connect('c:/Works/ClaudeCode/TradingStudio/data/bars_history.duckdb', read_only=True)
for tbl in ['bars_1min', 'bars_5min', 'bars_day']:
    cnt = c.execute(f"SELECT COUNT(*) FROM {tbl} WHERE instrument_id LIKE 'SA%' OR instrument_id LIKE 'FG%'").fetchone()[0]
    print(f'{tbl}: {cnt} CZCE rows')
c.close()
