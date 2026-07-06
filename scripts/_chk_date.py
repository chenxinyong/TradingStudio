import duckdb
c = duckdb.connect('c:/Works/ClaudeCode/TradingStudio/data/bars_history.duckdb', read_only=True)
for inst in ['sa000','fg000','ma000','ta000','rb000','ag000']:
    r = c.execute(f"SELECT MAX(trading_day) FROM bars_day WHERE instrument_id='{inst}'").fetchone()
    print(f'{inst}: last day = {r[0]}')
c.close()
