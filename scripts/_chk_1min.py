import duckdb
c = duckdb.connect('c:/Works/ClaudeCode/TradingStudio/data/bars_history.duckdb', read_only=True)
for inst in ['SA','FG','MA','TA']:
    cnt = c.execute(f"SELECT COUNT(*) FROM bars_1min WHERE instrument_id LIKE '{inst}%' AND bar_time >= '2026-06-01'").fetchone()[0]
    last = c.execute(f"SELECT MAX(bar_time) FROM bars_1min WHERE instrument_id LIKE '{inst}%'").fetchone()[0]
    print(f'{inst}: Jun rows={cnt}, last={last}')
c.close()
