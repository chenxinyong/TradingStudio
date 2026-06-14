"""
Verify bars_history.db import data quality.
Outputs to file to avoid console truncation.
"""
import sqlite3, os
from datetime import datetime

DB_PATH = r"C:\Works\ClaudeCode\TradingStudio\data\bars_history.db"
OUT = r"C:\Works\ClaudeCode\TradingStudio\scripts\verify_report.txt"

def price(v):
    return v / 10_000_000

def run(conn, sql, params=None):
    return conn.execute(sql, params or ())

report = []

def w(s=""):
    report.append(s)
    if len(report) % 20 == 0:
        print(".", end="", flush=True)

def r0(conn, sql, params=None):
    return run(conn, sql, params).fetchone()[0]

# ── Step 1: Table stats ──
def step1(conn):
    w(); w("=" * 60); w("1. TABLE STATS"); w("=" * 60)
    rc1 = r0(conn, "SELECT COUNT(*) FROM bars_1min")
    rc2 = r0(conn, "SELECT COUNT(*) FROM bars_day")
    ic1 = r0(conn, "SELECT COUNT(DISTINCT instrument_id) FROM bars_1min")
    ic2 = r0(conn, "SELECT COUNT(DISTINCT instrument_id) FROM bars_day")
    w(f"  bars_1min: {rc1:>12,} rows, {ic1} instruments")
    w(f"  bars_day:  {rc2:>12,} rows, {ic2} instruments")
    w(f"  TOTAL:     {rc1+rc2:>12,} rows")

    # Top-5 instruments per table
    for table in ["bars_1min", "bars_day"]:
        rows = run(conn, f"""
            SELECT instrument_id, COUNT(*) n, MIN(bar_time) first, MAX(bar_time) last
            FROM {table} GROUP BY instrument_id ORDER BY n DESC LIMIT 5
        """).fetchall()
        w(f"\n  [{table}] Top-5 by rows:")
        for r in rows:
            w(f"    {r[0]:20s} {r[1]:>10,} rows  [{r[2][:10]} -> {r[3][:10]}]")

# ── Step 2: Instruments ──
def step2(conn):
    w(); w("=" * 60); w("2. INSTRUMENTS"); w("=" * 60)
    for table in ["bars_1min", "bars_day"]:
        rows = run(conn, f"SELECT DISTINCT instrument_id FROM {table} ORDER BY instrument_id").fetchall()
        w(f"  [{table}] {len(rows)} instruments")
        # Show first 30 and last 10
        names = [r[0] for r in rows]
        w(f"    First: {', '.join(names[:15])}")
        w(f"    Last:  {', '.join(names[-10:])}")

# ── Step 3: Time range ──
def step3(conn):
    w(); w("=" * 60); w("3. TIME RANGE"); w("=" * 60)
    for table in ["bars_day", "bars_1min"]:
        rows = run(conn, f"""
            SELECT instrument_id,
                   MIN(bar_time) first, MAX(bar_time) last,
                   COUNT(*) n, COUNT(DISTINCT trading_day) td,
                   COUNT(DISTINCT substr(bar_time,1,10)) bd
            FROM {table} GROUP BY instrument_id ORDER BY first
        """).fetchall()
        w(f"\n  [{table}] {len(rows)} instruments:")
        for r in rows[:10]:
            inst, first, last, n, td, bd = r
            gap = " *** MISMATCH" if td != bd else ""
            w(f"    {inst:20s} {n:>8,} bars  [{first[:10]} -> {last[:10]}]  {td} td{gap}")
        if len(rows) > 10:
            w(f"    ... and {len(rows)-10} more instruments")

# ── Step 4: OHLCV sanity ──
def step4(conn):
    w(); w("=" * 60); w("4. OHLCV SANITY"); w("=" * 60)
    for table in ["bars_1min", "bars_day"]:
        w(f"\n  [{table}]")
        bad_hl = r0(conn, f"""
            SELECT COUNT(*) FROM {table}
            WHERE high < low OR high < open OR high < close OR low > open OR low > close
        """)
        bad_neg = r0(conn, f"""
            SELECT COUNT(*) FROM {table}
            WHERE open < 0 OR high < 0 OR low < 0 OR close < 0 OR volume < 0
        """)
        zero_vol = r0(conn, f"SELECT COUNT(*) FROM {table} WHERE volume = 0")
        zero_price = r0(conn, f"""
            SELECT COUNT(*) FROM {table} WHERE open = 0 OR high = 0 OR low = 0 OR close = 0
        """)
        nulls = r0(conn, f"""
            SELECT COUNT(*) FROM {table}
            WHERE instrument_id IS NULL OR bar_time IS NULL OR open IS NULL
        """)
        w(f"    High<Low / inverted:  {bad_hl:>8,}")
        w(f"    Negative values:      {bad_neg:>8,}")
        w(f"    Zero volume:          {zero_vol:>8,}")
        w(f"    Zero price:           {zero_price:>8,}")
        w(f"    NULL values:          {nulls:>8,}")

        # Bad samples
        bad = run(conn, f"""
            SELECT instrument_id, bar_time, open, high, low, close, volume
            FROM {table}
            WHERE high < low OR high < open OR high < close OR low > open OR low > close
               OR open < 0 OR low < 0 OR close < 0 OR volume < 0
            LIMIT 5
        """).fetchall()
        if bad:
            w(f"    --- Bad samples ---")
            for r in bad:
                w(f"      {r[0]} @ {r[1]}: O={price(r[2]):.4f} H={price(r[3]):.4f} L={price(r[4]):.4f} C={price(r[5]):.4f} V={r[6]}")
        else:
            w(f"    All bars pass basic checks OK")

        # Price jumps (>5% between adjacent bars) - sample one instrument
        sample_inst = run(conn, f"SELECT instrument_id FROM {table} LIMIT 1").fetchone()
        if sample_inst:
            inst = sample_inst[0]
            jumps = run(conn, f"""
                SELECT bar_time, close,
                       LAG(close) OVER (PARTITION BY instrument_id ORDER BY bar_time) prev_close
                FROM {table} WHERE instrument_id = ?
            """, (inst,)).fetchall()
            big = [(bt, price(pc), price(c)) for (bt, c, pc) in jumps
                   if pc and pc > 0 and abs(c - pc) * 1.0 / pc > 0.02]
            w(f"    [{inst}] jumps >2%: {len(big)} (sampled)")
            for bt, pc, c in big[:5]:
                pct = (c - pc) / pc * 100
                w(f"      {bt}: {pc:.4f} -> {c:.4f} ({pct:+.2f}%)")

# ── Step 5: Duplicates ──
def step5(conn):
    w(); w("=" * 60); w("5. DUPLICATES"); w("=" * 60)
    for table in ["bars_1min", "bars_day"]:
        dc = r0(conn, f"""
            SELECT COUNT(*) FROM (
                SELECT instrument_id, bar_time FROM {table}
                GROUP BY instrument_id, bar_time HAVING COUNT(*) > 1
            )
        """)
        dups = run(conn, f"""
            SELECT instrument_id, bar_time, COUNT(*) n
            FROM {table} GROUP BY instrument_id, bar_time HAVING n > 1
            LIMIT 10
        """).fetchall()
        w(f"  [{table}] duplicate keys: {dc}")
        for d in dups:
            w(f"    {d[0]} @ {d[1]} x {d[2]}")

# ── Step 6: Continuity ──
def step6(conn):
    w(); w("=" * 60); w("6. CONTINUITY (1min, sampled 3 instruments)"); w("=" * 60)
    insts = run(conn, "SELECT DISTINCT instrument_id FROM bars_1min ORDER BY instrument_id LIMIT 3").fetchall()
    for (inst,) in insts:
        gaps = run(conn, """
            SELECT prev_time, bar_time,
                   (strftime('%s', bar_time) - strftime('%s', prev_time)) gap_sec
            FROM (
                SELECT bar_time,
                       LAG(bar_time) OVER (PARTITION BY instrument_id ORDER BY bar_time) prev_time
                FROM bars_1min WHERE instrument_id = ?
            )
            WHERE prev_time IS NOT NULL AND gap_sec > 120
            ORDER BY gap_sec DESC LIMIT 10
        """, (inst,)).fetchall()
        w(f"  [{inst}] gaps >2min: {len(gaps)}")
        for g in gaps[:5]:
            sec = g[2]; m = sec // 60; h = m // 60
            w(f"    {g[0]} -> {g[1]}  ({sec}s = {h}h{m%60}m)")

# ── Step 7: Cross-check ──
def step7(conn):
    w(); w("=" * 60); w("7. DAY vs 1MIN CROSS-CHECK"); w("=" * 60)
    common = run(conn, """
        SELECT DISTINCT d.instrument_id FROM bars_day d
        INTERSECT
        SELECT DISTINCT m.instrument_id FROM bars_1min m
        LIMIT 1
    """).fetchall()
    if not common:
        w("  No common instruments")
        return
    inst = common[0][0]
    days = run(conn, """
        SELECT DISTINCT substr(bar_time,1,10) d FROM bars_day
        WHERE instrument_id = ? ORDER BY d DESC LIMIT 5
    """, (inst,)).fetchall()

    matched = mismatched = 0
    for (day,) in days:
        dbar = run(conn, """
            SELECT open, high, low, close, volume
            FROM bars_day WHERE instrument_id=? AND substr(bar_time,1,10)=?
        """, (inst, day)).fetchone()
        if not dbar: continue
        # Aggregate 1min -> day correctly:
        # O = first bar's open, H = MAX(high), L = MIN(low),
        # C = last bar's close, V = SUM(volume)
        first_o = run(conn, """
            SELECT open FROM bars_1min WHERE instrument_id=? AND substr(bar_time,1,10)=?
            ORDER BY bar_time LIMIT 1
        """, (inst, day)).fetchone()
        last_c = run(conn, """
            SELECT close FROM bars_1min WHERE instrument_id=? AND substr(bar_time,1,10)=?
            ORDER BY bar_time DESC LIMIT 1
        """, (inst, day)).fetchone()
        agg = run(conn, """
            SELECT MAX(high), MIN(low), SUM(volume), SUM(turnover), COUNT(*)
            FROM bars_1min WHERE instrument_id=? AND substr(bar_time,1,10)=?
        """, (inst, day)).fetchone()
        if not agg or agg[4] == 0: continue

        # 1min agg: first_open, max(high), min(low), last_close, sum(volume), sum(turnover), count
        m_o = first_o[0]; m_h = agg[0]; m_l = agg[1]; m_c = last_c[0]; m_v = agg[2]; m_to = agg[3]; m_bc = agg[4]

        w(f"\n  [{inst}] {day} (1min bars: {m_bc})")
        # dbar: open, high, low, close, volume (indices 0-4)
        checks = [
            ("H", dbar[1], m_h),
            ("L", dbar[2], m_l),
            ("O", dbar[0], m_o),
            ("C", dbar[3], m_c),
            ("V", dbar[4], m_v),
        ]
        all_ok = True
        for name, dv, mv in checks:
            if dv != mv:
                w(f"    *** {name}: day={price(dv) if name!='V' else dv} vs 1min={price(mv) if name!='V' else mv}")
                all_ok = False
        if all_ok:
            w(f"    OK: all match")
            matched += 1
        else:
            mismatched += 1
    w(f"\n  Summary: {matched} matched, {mismatched} mismatched out of {len(days)} days")

# ── Step 8: File info ──
def step8():
    w(); w("=" * 60); w("8. FILE INFO"); w("=" * 60)
    size = os.path.getsize(DB_PATH)
    w(f"  Path: {DB_PATH}")
    w(f"  Size: {size / 1048576:.1f} MB ({size:,} bytes)")
    for ext in ['-wal', '-shm']:
        p = DB_PATH + ext
        if os.path.exists(p):
            w(f"  {os.path.basename(p)}: {os.path.getsize(p):,} bytes")

# ── MAIN ──
def main():
    print(f"Verifying {DB_PATH} ...", flush=True)
    conn = sqlite3.connect(DB_PATH)
    try:
        step1(conn); print(" [1/8]", flush=True)
        step2(conn); print(" [2/8]", flush=True)
        step3(conn); print(" [3/8]", flush=True)
        step4(conn); print(" [4/8]", flush=True)
        step5(conn); print(" [5/8]", flush=True)
        step6(conn); print(" [6/8]", flush=True)
        step7(conn); print(" [7/8]", flush=True)
        step8(); print(" [8/8]", flush=True)
    finally:
        conn.close()

    # Write report
    with open(OUT, 'w', encoding='utf-8') as f:
        f.write(f"Verification Report — {datetime.now():%Y-%m-%d %H:%M:%S}\n")
        f.write(f"Database: {DB_PATH}\n\n")
        f.write('\n'.join(report))

    print(f"\nReport written to: {OUT}")
    # Also print the report to stdout
    print('\n'.join(report))

if __name__ == "__main__":
    main()
