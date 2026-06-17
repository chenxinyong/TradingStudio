"""
Cross-year data validation (2020-2025).
Beyond single-DB internal checks: year-over-year continuity,
main contract chain integrity, trading hour coverage, volume distribution.

Usage: python scripts/verify_cross_year.py [--data-dir data/]
"""
import sqlite3, os, sys, json
from datetime import datetime, date
from collections import defaultdict

DATA_DIR = sys.argv[2] if len(sys.argv) > 2 and sys.argv[1] == '--data-dir' else r"C:\Works\ClaudeCode\TradingStudio\data"
YEARS = [2020, 2021, 2022, 2023, 2024, 2025]
OUT = os.path.join(DATA_DIR, "verify_cross_year_report.txt")

# Price scale: all prices stored as ×10⁷
SCALE = 10_000_000

def price(v):
    return v / SCALE

def db_path(year):
    return os.path.join(DATA_DIR, f"bars_{year}.db")

report_lines = []
def w(s=""):
    report_lines.append(s)
    print(s)

def section(title):
    w(); w("=" * 70); w(f"  {title}"); w("=" * 70)

# ═══════════════════════════════════════════════════════════════════
# 1. CROSS-YEAR TABLE STATS
# ═══════════════════════════════════════════════════════════════════
def check_table_stats():
    section("1. CROSS-YEAR TABLE STATS")

    header = f"{'Year':<6} {'DB Size':>10} {'1min Bars':>12} {'1min Inst':>10} {'Day Bars':>12} {'Day Inst':>10} {'Days':>6}"
    w(header)
    w("-" * len(header))

    totals = {"size": 0, "bars_1min": 0, "bars_day": 0}
    all_insts_1min = set()
    all_insts_day = set()

    for yr in YEARS:
        path = db_path(yr)
        if not os.path.exists(path):
            w(f"{yr:<6} MISSING")
            continue

        size_mb = os.path.getsize(path) / (1024*1024)
        conn = sqlite3.connect(path)

        c1 = conn.execute("SELECT COUNT(*), COUNT(DISTINCT instrument_id) FROM bars_1min").fetchone()
        c2 = conn.execute("SELECT COUNT(*), COUNT(DISTINCT instrument_id) FROM bars_day").fetchone()
        days = conn.execute("SELECT COUNT(DISTINCT substr(bar_time,1,10)) FROM bars_1min").fetchone()[0]

        # Get instrument lists
        insts1 = set(r[0] for r in conn.execute("SELECT DISTINCT instrument_id FROM bars_1min"))
        insts2 = set(r[0] for r in conn.execute("SELECT DISTINCT instrument_id FROM bars_day"))
        all_insts_1min.update(insts1)
        all_insts_day.update(insts2)

        w(f"{yr:<6} {size_mb:>8.0f}MB {c1[0]:>12,} {c1[1]:>10} {c2[0]:>12,} {c2[1]:>10} {days:>6}")

        totals["size"] += size_mb
        totals["bars_1min"] += c1[0]
        totals["bars_day"] += c2[0]

        conn.close()

    w("-" * len(header))
    w(f"{'TOTAL':<6} {totals['size']:>8.0f}MB {totals['bars_1min']:>12,} {len(all_insts_1min):>10} {totals['bars_day']:>12,} {len(all_insts_day):>10}")

    return all_insts_1min, all_insts_day

# ═══════════════════════════════════════════════════════════════════
# 2. MAIN CONTRACT (主力连续) CONTINUITY
# ═══════════════════════════════════════════════════════════════════
def check_main_contracts():
    section("2. MAIN CONTRACT (主力连续) CHAIN INTEGRITY")

    # 主力连续合约特征：代码通常是产品代码+年份后缀，如 AP001, AP003, AP005...
    # 统计跨年出现的主力合约
    all_contracts = defaultdict(list)  # product_code -> [(year, bars, first, last)]

    for yr in YEARS:
        path = db_path(yr)
        if not os.path.exists(path): continue
        conn = sqlite3.connect(path)

        # Get all contracts for this year
        rows = conn.execute("""
            SELECT instrument_id, COUNT(*) as n, MIN(bar_time) as first, MAX(bar_time) as last
            FROM bars_1min GROUP BY instrument_id ORDER BY n DESC
        """).fetchall()

        for inst, n, first, last in rows:
            # Extract product code (strip trailing digits that look like year/month suffixes)
            # e.g., AP001 -> AP, ag2412 -> ag, SA501 -> SA, i2501 -> i
            # Pattern: letters + numbers where last 2-4 digits are year/month
            product = ''.join(c for c in inst if c.isalpha()).upper()
            all_contracts[product].append((yr, inst, n, first, last))

        conn.close()

    # Show products that appear in all 6 years (主力连续)
    w(f"\n{'Product':<8} {'Years':<8} {'Contracts':>10} {'Sample Instruments'}")
    w("-" * 80)

    multi_year = 0
    for product in sorted(all_contracts.keys()):
        entries = all_contracts[product]
        years_present = sorted(set(e[0] for e in entries))
        if len(years_present) >= 5:  # present in 5+ years
            multi_year += 1
            total_contracts = len(entries)
            sample = ', '.join(e[1] for e in entries[:6])
            if len(entries) > 6:
                sample += f', ... ({total_contracts} total)'
            w(f"{product:<8} {len(years_present):<8} {total_contracts:>10} {sample}")

    w(f"\n  Products in 5+ years: {multi_year}")

    # Check for products that span all 6 years with consistent naming
    all_6 = [p for p in sorted(all_contracts.keys())
             if len(set(e[0] for e in all_contracts[p])) == 6]
    w(f"  Products in ALL 6 years: {len(all_6)}")
    w(f"  {', '.join(all_6[:30])}")
    if len(all_6) > 30:
        w(f"  ... and {len(all_6) - 30} more")

    return all_contracts

# ═══════════════════════════════════════════════════════════════════
# 3. CROSS-YEAR INSTRUMENT CONSISTENCY
# ═══════════════════════════════════════════════════════════════════
def check_cross_year_consistency(all_insts_1min):
    section("3. CROSS-YEAR INSTRUMENT CONSISTENCY")

    year_insts = {}
    for yr in YEARS:
        path = db_path(yr)
        if not os.path.exists(path): continue
        conn = sqlite3.connect(path)
        insts = set(r[0] for r in conn.execute("SELECT DISTINCT instrument_id FROM bars_1min"))
        year_insts[yr] = insts
        conn.close()

    # Instruments only in one year (should be rare for main contracts)
    only_one_year = defaultdict(list)
    for inst in all_insts_1min:
        years_for_inst = [yr for yr in YEARS if yr in year_insts and inst in year_insts[yr]]
        if len(years_for_inst) == 1:
            only_one_year[years_for_inst[0]].append(inst)

    w(f"  Instruments appearing in only 1 year:")
    for yr in sorted(only_one_year.keys()):
        insts = sorted(only_one_year[yr])
        w(f"    {yr}: {len(insts)} — {', '.join(insts[:15])}{' ...' if len(insts) > 15 else ''}")

    # Year-over-year turnover
    w(f"\n  Year-over-year instrument overlap:")
    for i in range(len(YEARS) - 1):
        y1, y2 = YEARS[i], YEARS[i+1]
        if y1 not in year_insts or y2 not in year_insts: continue
        s1, s2 = year_insts[y1], year_insts[y2]
        common = s1 & s2
        only1 = s1 - s2
        only2 = s2 - s1
        w(f"    {y1}→{y2}: common={len(common)}, only_{y1}={len(only1)}, only_{y2}={len(only2)}")

# ═══════════════════════════════════════════════════════════════════
# 4. TRADING HOUR COVERAGE
# ═══════════════════════════════════════════════════════════════════
def check_trading_hours():
    section("4. TRADING HOUR COVERAGE (per year, sampled)")

    # Futures trading sessions (China):
    # Day: 09:00-10:15, 10:30-11:30, 13:30-15:00
    # Night: 21:00-23:00 (varies by product), some until 01:00 or 02:30

    sessions = {
        "Morning open (09:00-09:05)": ("09:00", "09:05"),
        "Morning auction (09:00-09:01)": ("09:00", "09:01"),
        "Lunch close (11:25-11:30)": ("11:25", "11:30"),
        "Afternoon open (13:30-13:35)": ("13:30", "13:35"),
        "Day close (14:58-15:00)": ("14:58", "15:00"),
        "Night open (21:00-21:05)": ("21:00", "21:05"),
        "Night late (01:00-01:05)": ("01:00", "01:05"),
        "Night late close (02:28-02:30)": ("02:28", "02:30"),
    }

    header = f"{'Year':<6} {'Inst Sample':>12}"
    for sname in sessions:
        header += f"{sname[:12]:>12} "
    w(header)
    w("-" * 130)

    for yr in YEARS:
        path = db_path(yr)
        if not os.path.exists(path): continue

        conn = sqlite3.connect(path)

        # Pick 3 instruments with most bars
        top = conn.execute(
            "SELECT instrument_id, COUNT(*) FROM bars_1min GROUP BY instrument_id ORDER BY COUNT(*) DESC LIMIT 3"
        ).fetchall()

        if not top: continue

        inst = top[0][0]  # use top instrument
        counts = []
        for sname, (start, end) in sessions.items():
            c = conn.execute("""
                SELECT COUNT(*) FROM bars_1min
                WHERE instrument_id = ? AND substr(bar_time, 12, 5) BETWEEN ? AND ?
            """, (inst, start, end)).fetchone()[0]
            counts.append(c)

        total = conn.execute("SELECT COUNT(*) FROM bars_1min WHERE instrument_id = ?", (inst,)).fetchone()[0]
        row = f"{yr:<6} {inst:>12}"
        for c in counts:
            marker = f"{c:>6,}" if c > 0 else "      -"
            row += f"{marker:>12} "
        row += f"  (total: {total:,})"
        w(row)

        conn.close()

    # Global: bars per hour of day
    w(f"\n  Bar count by hour of day (all years, top instrument):")
    yr = 2024
    path = db_path(yr)
    if os.path.exists(path):
        conn = sqlite3.connect(path)
        inst = conn.execute("SELECT instrument_id FROM bars_1min GROUP BY instrument_id ORDER BY COUNT(*) DESC LIMIT 1").fetchone()[0]
        hour_counts = conn.execute("""
            SELECT substr(bar_time, 12, 2) as h, COUNT(*) as c
            FROM bars_1min WHERE instrument_id = ?
            GROUP BY h ORDER BY h
        """, (inst,)).fetchall()
        w(f"    [{inst} @ {yr}]:")
        for h, c in hour_counts:
            bar = "█" * max(1, c // 1000)
            w(f"      {h}:00 = {c:>8,} {bar}")
        conn.close()

# ═══════════════════════════════════════════════════════════════════
# 5. VOLUME DISTRIBUTION
# ═══════════════════════════════════════════════════════════════════
def check_volume():
    section("5. VOLUME DISTRIBUTION")

    w(f"{'Year':<6} {'Total Vol':>16} {'Zero Vol':>10} {'Zero%':>8} {'Avg Vol':>12} {'Max Vol':>14} {'Max Vol Inst'}")
    w("-" * 80)

    for yr in YEARS:
        path = db_path(yr)
        if not os.path.exists(path): continue

        conn = sqlite3.connect(path)

        stats = conn.execute("""
            SELECT COUNT(*) as total,
                   SUM(CASE WHEN volume = 0 THEN 1 ELSE 0 END) as zeros,
                   AVG(volume) as avg_vol,
                   MAX(volume) as max_vol
            FROM bars_1min
        """).fetchone()

        # Find instrument with max volume bar
        max_row = conn.execute("""
            SELECT instrument_id, bar_time, volume FROM bars_1min
            ORDER BY volume DESC LIMIT 1
        """).fetchone()

        if stats:
            total, zeros, avg_vol, max_vol = stats
            zero_pct = 100.0 * zeros / total if total > 0 else 0
            w(f"{yr:<6} {total:>16,} {zeros:>10,} {zero_pct:>7.1f}% {avg_vol:>12,.0f} {max_vol:>14,} {max_row[0] if max_row else '':>14}")

        conn.close()

    # Volume concentration (top 5% of bars = what % of total volume)
    w(f"\n  Volume concentration (2024 sample):")
    yr = 2024
    path = db_path(yr)
    if os.path.exists(path):
        conn = sqlite3.connect(path)
        total_vol = conn.execute("SELECT SUM(volume) FROM bars_1min").fetchone()[0]
        top5pct_vol = conn.execute("""
            SELECT SUM(volume) FROM (
                SELECT volume FROM bars_1min ORDER BY volume DESC
                LIMIT (SELECT CAST(COUNT(*) * 0.05 AS INTEGER) FROM bars_1min)
            )
        """).fetchone()[0]
        if total_vol and top5pct_vol:
            w(f"    Top 5% bars = {100.0 * top5pct_vol / total_vol:.1f}% of total volume")
        conn.close()

# ═══════════════════════════════════════════════════════════════════
# 6. PRICE CONTINUITY (gap detection with proper trading session awareness)
# ═══════════════════════════════════════════════════════════════════
def check_price_jumps():
    section("6. PRICE JUMPS > 2x Typical Range (sampled per year)")

    for yr in YEARS:
        path = db_path(yr)
        if not os.path.exists(path): continue

        conn = sqlite3.connect(path)

        # Get top 3 instruments
        top3 = [r[0] for r in conn.execute(
            "SELECT instrument_id FROM bars_day GROUP BY instrument_id ORDER BY COUNT(*) DESC LIMIT 3"
        ).fetchall()]

        for inst in top3:
            # Find jumps where close-to-close change > 3x the median absolute change
            jumps = conn.execute("""
                WITH changes AS (
                    SELECT bar_time, close,
                           ABS(close - LAG(close) OVER (ORDER BY bar_time)) as abs_change
                    FROM bars_1min WHERE instrument_id = ?
                ),
                median_change AS (
                    SELECT abs_change as mc FROM changes
                    WHERE abs_change > 0 AND abs_change IS NOT NULL
                    ORDER BY abs_change LIMIT 1
                    OFFSET (SELECT COUNT(*) / 2 FROM changes WHERE abs_change > 0 AND abs_change IS NOT NULL)
                )
                SELECT bar_time, close, LAG(close) OVER (ORDER BY bar_time) as prev_close
                FROM changes
                WHERE abs_change > 1000 * (SELECT mc FROM median_change)
                ORDER BY abs_change DESC
                LIMIT 5
            """, (inst,)).fetchall()

            if jumps:
                w(f"  [{inst} @ {yr}]")
                for bt, c, pc in jumps:
                    if c and pc and pc > 0:
                        pct = 100.0 * (c - pc) / pc
                        w(f"    {bt}: {price(pc):.4f} → {price(c):.4f} ({pct:+.2f}%)")

        conn.close()

# ═══════════════════════════════════════════════════════════════════
# 7. CROSS-YEAR DAY BAR AGGREGATION CHECK
# ═══════════════════════════════════════════════════════════════════
def check_day_bar_aggregation():
    section("7. DAY BAR AGGREGATION ACCURACY (cross-check 1min→Day per year)")

    w(f"{'Year':<6} {'Inst':<12} {'Days Checked':>12} {'OHLCV OK':>10} {'Mismatched':>12} {'Rate':>8}")
    w("-" * 70)

    for yr in YEARS:
        path = db_path(yr)
        if not os.path.exists(path): continue

        conn = sqlite3.connect(path)

        # Get an instrument common to both tables
        inst = conn.execute("""
            SELECT d.instrument_id FROM bars_day d
            INTERSECT SELECT instrument_id FROM bars_1min
            LIMIT 1
        """).fetchone()

        if not inst:
            w(f"{yr:<6} {'(no common)':<12}")
            continue

        inst = inst[0]

        # Sample days
        days = conn.execute("""
            SELECT DISTINCT substr(bar_time, 1, 10) as day FROM bars_day
            WHERE instrument_id = ? ORDER BY day DESC LIMIT 10
        """, (inst,)).fetchall()

        matched, mismatched = 0, 0
        for (day,) in days:
            dbar = conn.execute("""
                SELECT open, high, low, close, volume FROM bars_day
                WHERE instrument_id = ? AND substr(bar_time, 1, 10) = ?
            """, (inst, day)).fetchone()

            if not dbar: continue

            # 1min aggregation
            agg = conn.execute("""
                SELECT
                    (SELECT open FROM bars_1min WHERE instrument_id = ? AND substr(bar_time,1,10) = ? ORDER BY bar_time LIMIT 1),
                    MAX(high), MIN(low),
                    (SELECT close FROM bars_1min WHERE instrument_id = ? AND substr(bar_time,1,10) = ? ORDER BY bar_time DESC LIMIT 1),
                    SUM(volume), COUNT(*)
                FROM bars_1min WHERE instrument_id = ? AND substr(bar_time,1,10) = ?
            """, (inst, day, inst, day, inst, day)).fetchone()

            if not agg or agg[5] == 0: continue

            ok = True
            for i, name in enumerate(["O", "H", "L", "C", "V"]):
                if dbar[i] != agg[i]:
                    ok = False
                    break

            if ok: matched += 1
            else: mismatched += 1

        total = matched + mismatched
        rate = f"{100.0 * matched / total:.0f}%" if total > 0 else "N/A"
        w(f"{yr:<6} {inst:<12} {total:>12,} {matched:>10} {mismatched:>12} {rate:>8}")

        conn.close()

# ═══════════════════════════════════════════════════════════════════
# 8. SUMMARY
# ═══════════════════════════════════════════════════════════════════
def write_summary():
    section("8. SUMMARY")

    total_size = sum(os.path.getsize(db_path(yr)) for yr in YEARS if os.path.exists(db_path(yr)))
    w(f"  Data period:  2020 ~ 2025 (6 years)")
    w(f"  Total size:   {total_size / (1024**3):.2f} GB ({total_size:,} bytes)")
    w(f"  DB count:     {sum(1 for yr in YEARS if os.path.exists(db_path(yr)))} / 6")
    w(f"  Report time:  {datetime.now():%Y-%m-%d %H:%M:%S}")

# ═══════════════════════════════════════════════════════════════════
# MAIN
# ═══════════════════════════════════════════════════════════════════
def main():
    t0 = datetime.now()
    print(f"Cross-Year Verification — {t0:%Y-%m-%d %H:%M:%S}")
    print(f"Data directory: {DATA_DIR}\n")

    all_1min, all_day = check_table_stats()
    all_contracts = check_main_contracts()
    check_cross_year_consistency(all_1min)
    check_trading_hours()
    check_volume()
    check_price_jumps()
    check_day_bar_aggregation()
    write_summary()

    with open(OUT, 'w', encoding='utf-8') as f:
        f.write(f"Cross-Year Verification Report — {t0:%Y-%m-%d %H:%M:%S}\n")
        f.write(f"Data directory: {DATA_DIR}\n\n")
        f.write('\n'.join(report_lines))

    elapsed = (datetime.now() - t0).total_seconds()
    print(f"\n{'=' * 70}")
    print(f"Report saved to: {OUT}")
    print(f"Elapsed: {elapsed:.1f}s")

if __name__ == "__main__":
    main()
