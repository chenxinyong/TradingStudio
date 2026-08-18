import duckdb
c = duckdb.connect('c:/Works/ClaudeCode/TradingStudio/src/TradingStudio/bars.duckdb', read_only=True)
cnt = c.execute("SELECT COUNT(*) FROM bars_1min").fetchone()[0]
last = c.execute("SELECT MAX(bar_time) FROM bars_1min").fetchone()[0]
insts = c.execute("SELECT COUNT(DISTINCT instrument_id) FROM bars_1min").fetchone()[0]
print(f'bars_1min: {cnt} rows, {insts} instruments, last={last}')
# CZCE
czce = c.execute("SELECT COUNT(*) FROM bars_1min WHERE instrument_id LIKE 'SA%' OR instrument_id LIKE 'FG%'").fetchone()[0]
print(f'CZCE total: {czce}')
# Exchange breakdown from recent data
for ex, p in [('SHFE','cu2608'),('DCE','m2609'),('CZCE','SA608'),('CFFEX','IF2608'),('INE','sc2609')]:
    cnt = c.execute(f"SELECT COUNT(*) FROM bars_1min WHERE instrument_id LIKE '%{p}%'").fetchone()[0]
    print(f'  {ex}: {cnt}')
c.close()
