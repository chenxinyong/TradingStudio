import duckdb
c = duckdb.connect('c:/Works/ClaudeCode/TradingStudio/src/TradingStudio/bars.duckdb', read_only=True)
r = c.execute("SELECT COUNT(*) FROM bars_1min WHERE bar_time >= '2026-07-07 13:00:00'").fetchone()
print(f'Today 13:00+: {r[0]} rows')
r = c.execute("SELECT MAX(bar_time) FROM bars_1min").fetchone()
print(f'Last bar: {r[0]}')
# CZCE check
czce = c.execute("SELECT COUNT(*) FROM bars_1min WHERE (instrument_id LIKE 'SA%' OR instrument_id LIKE 'FG%') AND bar_time >= '2026-07-07 13:00:00'").fetchone()
print(f'CZCE today: {czce[0]}')
c.close()
