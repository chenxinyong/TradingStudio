"""直接聚合 CZCE 连续合约 bars_day 数据"""
import duckdb

DB = 'data/bars_history.duckdb'
PRODS = ['FG','MA','TA']  # SA already done

c = duckdb.connect(DB)

for prod in PRODS:
    iid = prod.lower() + '000'
    print(f'=== {prod} ({iid}) ===')

    # Clear existing
    for tbl in ['bars_1min','bars_day']:
        c.execute(f"DELETE FROM {tbl} WHERE instrument_id='{iid}'")

    # 1min: aggregate from per-contract bars
    c.execute(f"""
        INSERT INTO bars_1min(instrument_id, trading_day, bar_time, open, high, low, close, volume)
        SELECT '{iid}', trading_day, bar_time, FIRST(open), MAX(high), MIN(low), LAST(close), SUM(volume)
        FROM bars_1min
        WHERE instrument_id LIKE '{prod}%' AND instrument_id != '{iid}'
        GROUP BY bar_time, trading_day
    """)
    cnt = c.execute(f"SELECT COUNT(*) FROM bars_1min WHERE instrument_id='{iid}'").fetchone()[0]
    print(f'  bars_1min: {cnt}')

    # Day: aggregate from 1min
    c.execute(f"""
        INSERT INTO bars_day(instrument_id, trading_day, bar_time, open, high, low, close, volume)
        SELECT '{iid}', trading_day, MIN(bar_time), FIRST(open), MAX(high), MIN(low), LAST(close), SUM(volume)
        FROM bars_1min WHERE instrument_id='{iid}'
        GROUP BY trading_day
    """)
    cnt = c.execute(f"SELECT COUNT(*) FROM bars_day WHERE instrument_id='{iid}'").fetchone()[0]
    last = c.execute(f"SELECT MAX(trading_day) FROM bars_day WHERE instrument_id='{iid}'").fetchone()[0]
    print(f'  bars_day: {cnt} rows, last={last}')

c.close()
print('Done')
