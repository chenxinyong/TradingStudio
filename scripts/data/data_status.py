"""Comprehensive data status report for TradingStudio"""
import duckdb, os, sqlite3

BASE = os.path.dirname(__file__)
HISTORY = os.path.join(BASE, "..", "..", "data", "bars_history.duckdb")
LIVE_DB  = os.path.join(BASE, "..", "..", "src", "TradingStudio", "bin", "Debug", "bars_live.db")
TICK_DIR = os.path.join(BASE, "..", "..", "src", "TradingStudio", "TickData")

def hdr(title):
    print(f"\n{'='*60}")
    print(f"  {title}")
    print(f"{'='*60}")

# ── History DB ──
hdr("HISTORY DB: bars_history.duckdb")
if os.path.exists(HISTORY):
    sz = os.path.getsize(HISTORY) / 1e9
    con = duckdb.connect(HISTORY, read_only=True)

    for tbl in ['bars_1min', 'bars_5min', 'bars_15min', 'bars_day', 'bars_week']:
        r = con.execute(f"SELECT COUNT(*), MIN(bar_time), MAX(bar_time) FROM {tbl}").fetchone()
        insts = con.execute(f"SELECT COUNT(DISTINCT instrument_id) FROM {tbl}").fetchone()[0]
        print(f"  {tbl:12s} {r[0]:>14,} rows  {insts:>5} insts  {str(r[1])[:19]} ~ {str(r[2])[:19]}")

    # xxx000 only
    r = con.execute("SELECT COUNT(*), MIN(bar_time), MAX(bar_time) FROM bars_1min WHERE instrument_id LIKE '%000'").fetchone()
    insts = con.execute("SELECT COUNT(DISTINCT instrument_id) FROM bars_1min WHERE instrument_id LIKE '%000'").fetchone()[0]
    print(f"  {'(xxx000)':12s} {r[0]:>14,} rows  {insts:>5} insts  {str(r[1])[:19]} ~ {str(r[2])[:19]}")

    # Core 30 products
    core30 = ['ag','al','au','bu','c','cf','cs','cu','eb','eg','fg','fu','hc','i','j','jm','l','m','ma','ni','oi','p','pp','rb','rm','ru','sa','sp','sr','ta','v','y','zn','a','b','pg','lh','lu','sc','nr','sn','pb','ss','wr','br','ao','si','lc','ps','rr']
    has_data = []
    for p in core30:
        cnt = con.execute(f"SELECT COUNT(*) FROM bars_1min WHERE instrument_id = '{p}000'").fetchone()[0]
        if cnt > 0: has_data.append(p)
    print(f"\n  Core products with xxx000: {len(has_data)}/{len(core30)}")
    missing = set(core30) - set(has_data)
    if missing: print(f"  Missing: {missing}")

    con.close()
    print(f"  Size: {sz:.2f} GB")
else:
    print("  NOT FOUND")

# ── Live DB ──
hdr("LIVE DB: bars_live (collect/live)")
live_found = False
for path in [LIVE_DB, os.path.join(BASE, "..", "..", "src", "TradingStudio", "bars_live.db")]:
    if os.path.exists(path):
        live_found = True
        sz = os.path.getsize(path) / 1e6
        sl = sqlite3.connect(path)
        for tbl in ['bars_1min', 'bars_day']:
            try:
                r = sl.execute(f"SELECT COUNT(*), MIN(bar_time), MAX(bar_time) FROM {tbl}").fetchone()
                insts = sl.execute(f"SELECT COUNT(DISTINCT instrument_id) FROM {tbl}").fetchone()[0]
                print(f"  {tbl:12s} {r[0]:>14,} rows  {insts:>5} insts  {r[1][:19] if r[1] else 'N/A'} ~ {r[2][:19] if r[2] else 'N/A'}")
            except: print(f"  {tbl}: not found")
        sl.close()
        print(f"  Path: {path}  Size: {sz:.1f} MB")
        break
if not live_found:
    print("  NOT FOUND (Live/Collect not running or not yet started)")

# ── TickData ──
hdr("TICK CSV: TickData/")
if os.path.exists(TICK_DIR):
    total_files = 0
    total_size = 0
    dates = set()
    for root, dirs, files in os.walk(TICK_DIR):
        for f in files:
            if f.endswith('.csv'):
                total_files += 1
                total_size += os.path.getsize(os.path.join(root, f))
                # Extract date: xxx_YYYYMMDD.csv
                if '_' in f:
                    d = f.rsplit('_', 1)[-1].replace('.csv', '')
                    if len(d) == 8: dates.add(d)
    print(f"  Files: {total_files}  Size: {total_size/1e6:.1f} MB  Dates: {len(dates)}")
    if dates:
        sd = sorted(dates)
        print(f"  Range: {sd[0]} ~ {sd[-1]}")
        # Count by exchange
        for d in os.listdir(TICK_DIR):
            dp = os.path.join(TICK_DIR, d)
            if os.path.isdir(dp):
                cnt = len([f for f in os.listdir(dp) if f.endswith('.csv')])
                print(f"    {d}: {cnt} files")
else:
    print("  NOT FOUND")

# ── Available Jinshuyuan Daily RARs ──
JINSHU = "C:/Works/Datas/Jinshuyuan/Daily"
hdr("JINSHUYUAN DAILY RARs")
if os.path.exists(JINSHU):
    rars = sorted([f for f in os.listdir(JINSHU) if f.endswith('.rar')])
    print(f"  Files: {len(rars)}")
    # Group by month
    months = {}
    for r in rars:
        m = r[:6]
        months[m] = months.get(m, 0) + 1
    for m, cnt in sorted(months.items()):
        print(f"    {m}: {cnt} files")
    # Missing dates
    if rars:
        all_dates = set(r[:8] for r in rars)
        # June 2026: expected 1-23 excluding weekends
        import datetime
        expected = []
        d = datetime.date(2026, 6, 1)
        while d <= datetime.date(2026, 6, 23):
            if d.weekday() < 5: expected.append(d.strftime('%Y%m%d'))
            d += datetime.timedelta(days=1)
        missing_dates = [e for e in expected if e not in all_dates]
        if missing_dates: print(f"  Missing June dates: {missing_dates}")
else:
    print("  NOT FOUND")

print()
print("DONE")
