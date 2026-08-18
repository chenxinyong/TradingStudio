import duckdb
c=duckdb.connect('c:/Works/ClaudeCode/TradingStudio/src/TradingStudio/bars.duckdb',read_only=True)
rows=c.execute("SELECT DISTINCT instrument_id FROM bars_1min ORDER BY instrument_id").fetchall()
insts=[r[0] for r in rows]
exch={}
for i in insts:
    prefix = i[:2] if i[0].isupper() else i[:1]
    exch[prefix]=exch.get(prefix,0)+1
for k,v in sorted(exch.items()):
    print(f'{k}: {v}')
print(f'Total: {len(insts)}')
c.close()
