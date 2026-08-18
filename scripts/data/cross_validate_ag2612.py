"""
自动化交叉验证 — ag2612 5min Bar
1. 从 AKShare/Sina 获取参考 K 线数据
2. 从 DuckDB bars_1min 聚合成 5min Bar
3. 逐 Bar 比对 OHLCV
4. 输出报告

用法:
    python scripts/cross_validate_ag2612.py [--db <path>]
"""

import sys
import io
import csv
import os
from datetime import datetime
from pathlib import Path

# UTF-8 stdout for Windows
if sys.stdout.encoding != 'utf-8':
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

# ──── 配置 ────
INSTRUMENT = "ag2612"
PRODUCT = "ag"
TICK_SIZE = 1.0  # 白银 1 元/千克
PERIOD = "5min"
TOLERANCE = 1  # tick(s)
VOL_THRESHOLD = 0.03  # 3% — 成交量差异可能较大（数据源不同）
TIME_TOL_SEC = 60  # 时间对齐容差

# 数据路径
REPO_ROOT = Path(__file__).resolve().parent.parent.parent
DB_PATH = REPO_ROOT / "src" / "TradingStudio" / "data" / "bars_live.duckdb"
OUT_DIR = REPO_ROOT / "data" / "crosscheck"
OURS_CSV = OUT_DIR / f"{INSTRUMENT}_5min_ours.csv"
REF_CSV = OUT_DIR / f"{INSTRUMENT}_5min_ref.csv"
REPORT_CSV = OUT_DIR / f"{INSTRUMENT}_5min_report.csv"
REPORT_MD = REPO_ROOT / "docs" / "research" / "cross-validation" / f"{INSTRUMENT}-5min-2026-07.md"

os.makedirs(OUT_DIR, exist_ok=True)


def fetch_reference():
    """从 AKShare/Sina 获取参考 5min K 线"""
    print(f"[1/4] 获取参考数据: {INSTRUMENT} {PERIOD}...")
    import akshare as ak

    # Sina 期货分钟 K 线
    df = ak.futures_zh_minute_sina(symbol="AG2612", period="5")

    # 标准化列名
    df = df.rename(columns={
        "datetime": "bar_time",
        "open": "open",
        "high": "high",
        "low": "low",
        "close": "close",
        "volume": "volume",
        "hold": "open_interest",
    })
    df["instrument_id"] = INSTRUMENT
    df["trading_day"] = ""  # Sina 不提供交易日

    # 只保留需要的列
    cols = ["instrument_id", "bar_time", "trading_day",
            "open", "high", "low", "close", "volume", "open_interest"]
    df = df[cols]

    # 保存
    df.to_csv(REF_CSV, index=False, encoding="utf-8-sig")
    print(f"  ✓ 参考数据: {len(df)} rows → {REF_CSV}")
    print(f"    时间范围: {df['bar_time'].iloc[0]} ~ {df['bar_time'].iloc[-1]}")
    return df


def export_ours():
    """从 DuckDB bars_1min 聚合成 5min Bar 并导出"""
    print(f"\n[2/4] 导出我方数据: {INSTRUMENT} {PERIOD}...")
    import duckdb

    con = duckdb.connect(str(DB_PATH), read_only=True)

    # 检查是否有 bars_5min 表
    tables = [r[0] for r in con.execute("SHOW TABLES").fetchall()]
    has_5min = "bars_5min" in tables
    print(f"    可用表: {', '.join(tables)}")

    if has_5min:
        # Check if ag2612 data exists in bars_5min
        count = con.execute(
            f"SELECT COUNT(*) FROM bars_5min WHERE instrument_id ILIKE '{INSTRUMENT}'"
        ).fetchone()[0]
        if count > 0:
            print(f"    使用 bars_5min 表 ({count} rows)")
            sql = f"""
                SELECT instrument_id, bar_time, trading_day,
                       open, high, low, close, volume, turnover, open_interest, tick_count
                FROM bars_5min
                WHERE instrument_id ILIKE '{INSTRUMENT}'
                ORDER BY bar_time
            """
        else:
            print(f"    bars_5min 中无 {INSTRUMENT}，从 bars_1min 聚合 5min Bar")
            sql = f"""
                SELECT
                    instrument_id,
                    time_bucket(INTERVAL '5 minutes', CAST(bar_time AS TIMESTAMP)) as bar_time,
                    MIN(CAST(trading_day AS VARCHAR)) as trading_day,
                    FIRST(open) as open,
                    MAX(high) as high,
                    MIN(low) as low,
                    LAST(close) as close,
                    SUM(volume) as volume,
                    SUM(turnover) as turnover,
                    LAST(open_interest) as open_interest,
                    SUM(tick_count) as tick_count
                FROM bars_1min
                WHERE instrument_id ILIKE '{INSTRUMENT}'
                GROUP BY instrument_id, time_bucket(INTERVAL '5 minutes', CAST(bar_time AS TIMESTAMP))
                ORDER BY bar_time
            """
    else:
        print("    从 bars_1min 聚合 5min Bar")
        sql = f"""
            SELECT
                instrument_id,
                time_bucket(INTERVAL '5 minutes', CAST(bar_time AS TIMESTAMP)) as bar_time,
                MIN(CAST(trading_day AS VARCHAR)) as trading_day,
                FIRST(open) as open,
                MAX(high) as high,
                MIN(low) as low,
                LAST(close) as close,
                SUM(volume) as volume,
                SUM(turnover) as turnover,
                LAST(open_interest) as open_interest,
                SUM(tick_count) as tick_count
            FROM bars_1min
            WHERE instrument_id ILIKE '{INSTRUMENT}'
            GROUP BY instrument_id, time_bucket(INTERVAL '5 minutes', CAST(bar_time AS TIMESTAMP))
            ORDER BY bar_time
        """

    rows = con.execute(sql).fetchall()
    cols = [d[0] for d in con.description]
    con.close()

    if not rows:
        print(f"  ✗ 未找到 {INSTRUMENT} 数据")
        return None

    # 保存 CSV（价格转换为小数）
    PRICE_SCALE = 10_000_000
    with open(OURS_CSV, "w", newline="", encoding="utf-8-sig") as f:
        writer = csv.writer(f)
        writer.writerow(["instrument_id", "bar_time", "trading_day",
                         "open", "high", "low", "close",
                         "volume", "turnover", "open_interest", "tick_count"])

        for row in rows:
            inst_id = row[0]
            bar_time = str(row[1])
            trading_day = str(row[2]) if row[2] else ""

            # 检查价格是否需要除 scale
            o = row[3]
            if isinstance(o, (int, float)) and abs(o) > 1_000_000:
                # 原始 BIGINT 值，需要除 PRICE_SCALE
                open_px = o / PRICE_SCALE
                high_px = row[4] / PRICE_SCALE
                low_px = row[5] / PRICE_SCALE
                close_px = row[6] / PRICE_SCALE
            else:
                open_px = float(o)
                high_px = float(row[4])
                low_px = float(row[5])
                close_px = float(row[6])

            volume = int(row[7]) if row[7] else 0
            turnover = float(row[8]) if row[8] else 0
            oi = float(row[9]) if row[9] else 0
            tc = int(row[10]) if row[10] else 0

            writer.writerow([inst_id, bar_time, trading_day,
                             f"{open_px:.1f}", f"{high_px:.1f}",
                             f"{low_px:.1f}", f"{close_px:.1f}",
                             volume, f"{turnover:.1f}", f"{oi:.1f}", tc])

    print(f"  ✓ 我方数据: {len(rows)} rows → {OURS_CSV}")
    if rows:
        print(f"    时间范围: {rows[0][1]} ~ {rows[-1][1]}")
    return rows


def is_trading_time(dt: datetime) -> bool:
    """检查是否为上期所 (SHFE) 交易时段（含夜盘）"""
    t = dt.time()
    h = dt.hour
    m = dt.minute
    hm = h * 100 + m

    # 上午: 09:00-10:15 (915), 10:30-11:30 (1130)
    if 915 <= hm < 1015:
        return True
    if 1030 <= hm < 1130:
        return True
    # 下午: 13:30-15:00 (1500)
    if 1330 <= hm < 1500:
        return True
    # 夜盘: 21:00-02:30 (次日凌晨)
    if hm >= 2100 or hm < 230:  # 230 = 02:30
        return True
    # 集合竞价 (08:55-09:00) — Sina 有数据但可能不准确，排除
    return False


def run_comparison():
    """运行交叉比对"""
    print(f"\n[3/4] 逐 Bar 比对...")

    # 加载双方 CSV
    def load_csv(path, label):
        rows = []
        with open(path, "r", encoding="utf-8-sig") as f:
            reader = csv.DictReader(f)
            for r in reader:
                bt_str = r.get("bar_time", "").strip()
                try:
                    bt = datetime.strptime(bt_str[:19], "%Y-%m-%d %H:%M:%S")
                except ValueError:
                    continue
                rows.append({
                    "bar_time": bt,
                    "open": float(r.get("open", 0)),
                    "high": float(r.get("high", 0)),
                    "low": float(r.get("low", 0)),
                    "close": float(r.get("close", 0)),
                    "volume": float(r.get("volume", 0)),
                    "instrument_id": r.get("instrument_id", ""),
                })
        print(f"    加载 {label}: {len(rows)} rows (原始)")
        return rows

    ours_raw = load_csv(OURS_CSV, "TradingStudio")
    ref_raw = load_csv(REF_CSV, "Sina/AKShare")

    if not ours_raw or not ref_raw:
        print("  ✗ 数据为空")
        return None

    # 查找重叠的日期范围
    ours_dates = set(dt.date() for dt in [r["bar_time"] for r in ours_raw])
    ref_dates = set(dt.date() for dt in [r["bar_time"] for r in ref_raw])
    overlap_dates = ours_dates & ref_dates
    print(f"    我方日期: {min(ours_dates)} ~ {max(ours_dates)} ({len(ours_dates)} days)")
    print(f"    参考日期: {min(ref_dates)} ~ {max(ref_dates)} ({len(ref_dates)} days)")
    print(f"    重叠日期: {len(overlap_dates)} days — {min(overlap_dates) if overlap_dates else 'N/A'} ~ {max(overlap_dates) if overlap_dates else 'N/A'}")

    if not overlap_dates:
        print("  ✗ 没有重叠日期")
        return None

    # 过滤：仅保留重叠日期 + 交易时段
    ours = [r for r in ours_raw
            if r["bar_time"].date() in overlap_dates and is_trading_time(r["bar_time"])]
    ref = [r for r in ref_raw
           if r["bar_time"].date() in overlap_dates and is_trading_time(r["bar_time"])]

    print(f"    过滤后 TradingStudio: {len(ours)} rows")
    print(f"    过滤后 Sina/AKShare:  {len(ref)} rows")

    # 时间对齐 — 只做精确匹配（5min Bar 时间应该完全一致）
    ours_by_time = {}
    for r in ours:
        t = r["bar_time"]
        if t not in ours_by_time:
            ours_by_time[t] = []
        ours_by_time[t].append(r)

    ref_by_time = {}
    for r in ref:
        t = r["bar_time"]
        if t not in ref_by_time:
            ref_by_time[t] = []
        ref_by_time[t].append(r)

    all_times = sorted(set(list(ours_by_time.keys()) + list(ref_by_time.keys())))

    matched = []
    ours_only = []
    ref_only = []

    for t in all_times:
        if t in ours_by_time and t in ref_by_time:
            for o in ours_by_time[t]:
                for r in ref_by_time[t]:
                    matched.append((o, r, t))
        elif t in ours_by_time:
            for o in ours_by_time[t]:
                ours_only.append((o, t))
        else:
            for r in ref_by_time[t]:
                ref_only.append((r, t))

    print(f"    精确匹配: {len(matched)}, 仅我方: {len(ours_only)}, 仅参考: {len(ref_only)}")

    # 逐 Bar 比较
    results = []
    ohlc_bad = {f: 0 for f in ["open", "high", "low", "close"]}
    vol_bad = 0

    for o, r, aligned_t in matched:
        row = {
            "bar_time": aligned_t.strftime("%Y-%m-%d %H:%M:%S"),
            "status": "OK",
        }
        is_bad = False
        for field in ["open", "high", "low", "close"]:
            o_val = o.get(field, 0)
            r_val = r.get(field, 0)
            diff = o_val - r_val
            diff_ticks = abs(diff) / TICK_SIZE if TICK_SIZE > 0 else abs(diff)
            row[f"{field}_ours"] = o_val
            row[f"{field}_ref"] = r_val
            row[f"{field}_diff"] = round(diff, 4)
            row[f"{field}_ticks"] = round(diff_ticks, 2)
            if diff_ticks > TOLERANCE:
                ohlc_bad[field] += 1
                is_bad = True

        # Volume
        o_vol = o.get("volume", 0)
        r_vol = r.get("volume", 0)
        max_v = max(abs(o_vol), abs(r_vol), 1)
        vol_pct = abs(o_vol - r_vol) / max_v * 100
        row["volume_ours"] = o_vol
        row["volume_ref"] = r_vol
        row["volume_diff"] = round(o_vol - r_vol, 2)
        row["volume_pct"] = round(vol_pct, 2)
        if vol_pct > VOL_THRESHOLD * 100:
            vol_bad += 1
            is_bad = True

        if is_bad:
            row["status"] = "MISMATCH"
        results.append(row)

    # 保存详细报告
    fieldnames = ["bar_time", "status",
                  "open_ours", "open_ref", "open_diff", "open_ticks",
                  "high_ours", "high_ref", "high_diff", "high_ticks",
                  "low_ours", "low_ref", "low_diff", "low_ticks",
                  "close_ours", "close_ref", "close_diff", "close_ticks",
                  "volume_ours", "volume_ref", "volume_diff", "volume_pct"]
    with open(REPORT_CSV, "w", newline="", encoding="utf-8-sig") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames, extrasaction="ignore")
        writer.writeheader()
        for r in results:
            writer.writerow(r)
    print(f"    详细报告: {REPORT_CSV}")

    return {
        "results": results,
        "ours_only": ours_only,
        "ref_only": ref_only,
        "ohlc_bad": ohlc_bad,
        "vol_bad": vol_bad,
        "n_matched": len(matched),
        "n_ours_only": len(ours_only),
        "n_ref_only": len(ref_only),
    }


def write_report(stats):
    """生成交叉验证报告"""
    print(f"\n[4/4] 生成报告...")

    results = stats["results"]
    n = stats["n_matched"]

    # 计算统计
    lines = []
    lines.append(f"# {INSTRUMENT} 5min K线交叉验证报告")
    lines.append("")
    lines.append(f"**日期**: {datetime.now().strftime('%Y-%m-%d')}")
    lines.append(f"**参考数据源**: AKShare / Sina 期货分钟 K线")
    lines.append(f"**我方数据源**: TradingStudio DuckDB (bars_1min → 5min 聚合)")
    lines.append("")
    lines.append("## 概览")
    lines.append("")
    lines.append("| 指标 | 值 |")
    lines.append("|------|-----|")
    lines.append(f"| 匹配 Bar 数 | {n} |")
    lines.append(f"| 仅我方存在 | {stats['n_ours_only']} |")
    lines.append(f"| 仅参考存在 | {stats['n_ref_only']} |")

    total_bad = sum(1 for r in results if r.get("open_ticks", 0) > TOLERANCE or
                    r.get("high_ticks", 0) > TOLERANCE or
                    r.get("low_ticks", 0) > TOLERANCE or
                    r.get("close_ticks", 0) > TOLERANCE)
    ohlc_rate = total_bad / n * 100 if n else 0
    lines.append(f"| OHLC 异常 Bar | {total_bad} ({ohlc_rate:.2f}%) |")
    lines.append(f"| Volume 异常 Bar | {stats['vol_bad']} ({stats['vol_bad']/n*100:.2f}%) |")
    lines.append("")

    # OHLC 统计
    lines.append("## OHLC 逐字段统计")
    lines.append("")
    lines.append("| 字段 | 异常数 | 异常率 | 平均差异(tick) | 最大差异(tick) |")
    lines.append("|------|--------|--------|---------------|---------------|")
    for field in ["open", "high", "low", "close"]:
        diffs = [r[f"{field}_ticks"] for r in results]
        avg_d = sum(diffs) / len(diffs) if diffs else 0
        max_d = max(diffs) if diffs else 0
        bad_n = stats["ohlc_bad"][field]
        bad_pct = bad_n / n * 100 if n else 0
        lines.append(f"| {field.upper()} | {bad_n} | {bad_pct:.2f}% | {avg_d:.2f}t | {max_d:.2f}t |")
    lines.append("")

    # 成交量统计
    vol_diffs = [r["volume_pct"] for r in results]
    avg_vol = sum(vol_diffs) / len(vol_diffs) if vol_diffs else 0
    max_vol = max(vol_diffs) if vol_diffs else 0
    lines.append(f"| VOLUME | {stats['vol_bad']} | {stats['vol_bad']/n*100:.2f}% | {avg_vol:.2f}% | {max_vol:.2f}% |")
    lines.append("")

    # 总体评级
    if ohlc_rate < 0.5 and stats['vol_bad'] / max(n, 1) < 0.05:
        rating = "PASS [OK]"
    elif ohlc_rate < 2.0:
        rating = "WARN [!]"
    else:
        rating = "FAIL [X]"

    lines.append(f"## 总体评级: {rating}")
    lines.append("")

    # Top 差异
    bad_rows = sorted(
        [r for r in results if r["status"] == "MISMATCH"],
        key=lambda r: max(r.get(f"{f}_ticks", 0) for f in ["open", "high", "low", "close"]),
        reverse=True,
    )[:10]

    if bad_rows:
        lines.append("## Top 10 差异最大的 Bar")
        lines.append("")
        lines.append("| Time | Ours Close | Ref Close | Diff | Ticks |")
        lines.append("|------|-----------|----------|------|-------|")
        for r in bad_rows[:10]:
            lines.append(f"| {r['bar_time']} | {r['close_ours']:.1f} | {r['close_ref']:.1f} | {r['close_diff']:.1f} | {r['close_ticks']:.1f}t |")
        lines.append("")

    # 仅我方/仅参考
    if stats["ours_only"]:
        lines.append("## 仅在我方存在的 Bar")
        lines.append("")
        for o, t in stats["ours_only"][:5]:
            lines.append(f"- {t.strftime('%Y-%m-%d %H:%M:%S')}")
        if len(stats["ours_only"]) > 5:
            lines.append(f"- ... 还有 {len(stats['ours_only']) - 5} 条")
        lines.append("")

    if stats["ref_only"]:
        lines.append("## 仅在参考存在的 Bar")
        lines.append("")
        for r, t in stats["ref_only"][:5]:
            lines.append(f"- {t.strftime('%Y-%m-%d %H:%M:%S')}")
        if len(stats["ref_only"]) > 5:
            lines.append(f"- ... 还有 {len(stats['ref_only']) - 5} 条")
        lines.append("")

    lines.append("---")
    lines.append("")
    lines.append("### 已知差异来源")
    lines.append("")
    lines.append("1. **数据源不同**: TradingStudio 数据来自 CTP 金数源，参考数据来自 Sina 行情")
    lines.append("2. **成交量为 delta 值**: 双方均使用单 Bar 内成交量增量，但聚合粒度可能不同")
    lines.append("3. **夜盘 Bar 时间**: Sina 夜盘 Bar 的时间戳可能与 CTP 不同")
    lines.append("4. **集合竞价**: 08:55-09:00 的 Bar 可能在不同平台表现不同")

    report_md = "\n".join(lines)
    REPORT_MD.parent.mkdir(parents=True, exist_ok=True)
    with open(REPORT_MD, "w", encoding="utf-8") as f:
        f.write(report_md)

    print(f"    报告: {REPORT_MD}")
    print()
    print(report_md)


def main():
    print("=" * 56)
    print(f"  交叉验证: {INSTRUMENT} {PERIOD}")
    print("=" * 56)

    fetch_reference()
    our_data = export_ours()
    if not our_data:
        print("\n✗ 我方数据导出失败，终止")
        return
    stats = run_comparison()
    if stats:
        write_report(stats)

    # 清理临时文件
    # 保留 CSV 方便后续分析
    print(f"\n输出文件:")
    print(f"  参考数据: {REF_CSV}")
    print(f"  我方数据: {OURS_CSV}")
    print(f"  详细比对: {REPORT_CSV}")
    print(f"  验证报告: {REPORT_MD}")


if __name__ == "__main__":
    main()
