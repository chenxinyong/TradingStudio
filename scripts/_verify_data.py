"""数据完整性验证"""
import duckdb, os

DB = 'c:/Works/ClaudeCode/TradingStudio/src/TradingStudio/bars.duckdb'
TD = 'c:/Works/ClaudeCode/TradingStudio/src/TradingStudio/TickData'
c = duckdb.connect(DB, read_only=True)

print("=" * 65)
print("1) Tick CSV")
print("=" * 65)
for ex in ['SHFE','DCE','CZCE','CFFEX','INE','GFEX']:
    d = os.path.join(TD, ex)
    if os.path.exists(d):
        files = len([f for f in os.listdir(d) if f.endswith('.csv')])
        size = sum(os.path.getsize(os.path.join(d,f)) for f in os.listdir(d) if f.endswith('.csv'))
        print(f"  {ex:<6}: {files:>4} files, {size/1024/1024:>6.1f}MB")
    else:
        print(f"  {ex:<6}: MISSING")

print(f"\n{'='*65}")
print("2) 普通合约 Bar")
print(f"{'='*65}")
for tbl, time_col in [('bars_1min','bar_time'),('bars_day','trading_day')]:
    print(f"\n  --- {tbl} ---")
    for ex, pat in [('SHFE','cu%'),('DCE','m%'),('CZCE','TA%'),('CFFEX','IF%'),('INE','sc%'),('GFEX','si%')]:
        cnt = c.execute(f"SELECT COUNT(*) FROM {tbl} WHERE instrument_id LIKE '{pat}'").fetchone()[0]
        insts = c.execute(f"SELECT COUNT(DISTINCT instrument_id) FROM {tbl} WHERE instrument_id LIKE '{pat}'").fetchone()[0]
        last = c.execute(f"SELECT MAX({time_col}) FROM {tbl} WHERE instrument_id LIKE '{pat}'").fetchone()[0]
        print(f"    {ex:<6}: {cnt:>8} bars, {insts:>3} contracts, last={str(last)[:16]}")

print(f"\n{'='*65}")
print("3) 连续合约 xxx000")
print(f"{'='*65}")
for tbl, time_col in [('bars_1min','bar_time'),('bars_5min','bar_time'),('bars_15min','bar_time'),('bars_day','trading_day'),('bars_week','trading_day')]:
    cnt = c.execute(f"SELECT COUNT(*) FROM {tbl} WHERE instrument_id LIKE '%000'").fetchone()[0]
    insts = c.execute(f"SELECT COUNT(DISTINCT instrument_id) FROM {tbl} WHERE instrument_id LIKE '%000'").fetchone()[0]
    last = c.execute(f"SELECT MAX({time_col}) FROM {tbl} WHERE instrument_id LIKE '%000'").fetchone()[0]
    print(f"  {tbl:<12}: {cnt:>8} bars, {insts:>3} contracts, last={str(last)[:16]}")

print(f"\n  --- 重点品种 ---")
for iid in ['rb000','ag000','sa000','fg000','ma000','ta000','cu000','i000','m000','sc000','IF000']:
    for tbl, tc in [('bars_1min','bar_time'),('bars_day','trading_day')]:
        cnt = c.execute(f"SELECT COUNT(*) FROM {tbl} WHERE instrument_id='{iid}'").fetchone()[0]
        last = c.execute(f"SELECT MAX({tc}) FROM {tbl} WHERE instrument_id='{iid}'").fetchone()[0]
        print(f"    {iid:<8} {tbl:<12}: {cnt:>8} bars, last={str(last)[:16]}")
    print()

c.close()
