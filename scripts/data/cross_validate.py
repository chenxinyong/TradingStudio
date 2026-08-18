"""
交叉验证脚本 — 将 TradingStudio 导出的 Bar CSV 与文华/博易等外部参考数据逐 Bar 比对。

用法:
    python scripts/cross_validate.py \
        --ours data/export/ag2612_5min.csv \
        --ref data/ref/wenhua_ag2612_5min.csv \
        --instrument ag2612 \
        --tolerance 1 \
        --output cross_report.csv

参考数据列名映射:
    默认期望参考 CSV 列名为: bar_time, open, high, low, close, volume
    如果文华/博易导出的列名不同，使用 --col-mapping 映射:
    --col-mapping "time=时间,open=开盘,high=最高,low=最低,close=收盘,volume=成交量"
"""

import argparse
import sys
import csv
import re
import io
from datetime import datetime, timedelta
from pathlib import Path
from typing import Optional

# Windows: force UTF-8 stdout to avoid UnicodeEncodeError on emoji/Chinese
if sys.stdout.encoding != 'utf-8':
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

# ═══════════════════════════════════════════
# 品种 Tick Size 配置（最小变动价位，元/单位）
# 来源: symbols.json + 六大交易所合约规格表
# ═══════════════════════════════════════════
TICK_SIZES = {
    # 上期所 SHFE
    "cu": 10,  "al": 5,  "zn": 5,  "pb": 5,
    "ni": 10, "sn": 10, "au": 0.02, "ag": 1,
    "rb": 1,  "hc": 1,  "ss": 5,  "bu": 1,
    "ru": 5,  "sp": 2,  "fu": 1,  "wr": 1,
    # 上能源 INE
    "sc": 0.1, "lu": 1,  "nr": 5, "br": 5, "bc": 0.1,
    # 大商所 DCE
    "m": 1,  "y": 1,  "a": 1,  "b": 1,
    "c": 1,  "cs": 1, "l": 1,  "v": 1,
    "pp": 1, "j": 0.5, "jm": 0.5, "i": 0.5,
    "eg": 1, "eb": 1, "pg": 1, "lh": 1,
    "p": 2,  "rr": 1, "jd": 1,
    # 郑商所 CZCE
    "ta": 2, "ma": 1, "fg": 1, "sa": 1,
    "cf": 5, "sr": 1, "oi": 1, "rm": 1,
    "cy": 5, "pf": 2, "ur": 1, "sm": 2,
    "sf": 2, "pk": 2, "cj": 5, "wh": 1,
    "pm": 1, "ri": 1, "lr": 1, "jr": 1,
    "rs": 1, "ap": 1, "sh": 1,
    # 中金所 CFFEX
    "if": 0.2, "ic": 0.2, "ih": 0.2, "im": 0.2,
    "ts": 0.005, "tf": 0.005, "t": 0.005, "tl": 0.01,
    # 广期所 GFEX
    "si": 5, "lc": 50, "ps": 5,
}


def extract_product_code(instrument_id: str) -> str:
    """从合约代码提取品种代码: 'ag2612' → 'ag', 'IF000' → 'IF'"""
    # 去掉末尾数字（包括 000 连续合约后缀）
    m = re.match(r'^([A-Za-z]+)', instrument_id)
    return m.group(1).lower() if m else instrument_id.lower()


def get_tick_size(instrument_id: str) -> float:
    """获取品种最小变动价位"""
    code = extract_product_code(instrument_id)
    return TICK_SIZES.get(code, 1.0)


def parse_args():
    p = argparse.ArgumentParser(
        description="交叉验证: 将 TradingStudio Bar CSV 与外部参考数据逐 Bar 比对",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
示例:
  python cross_validate.py --ours ag2612_5min.csv --ref wh_ag2612_5min.csv --instrument ag2612
  python cross_validate.py --ours rb000_day.csv --ref boyi_rb_day.csv --instrument rb000 --tolerance 2

参考数据列名映射:
  如果参考 CSV 的列名与默认不同，用 --col-mapping 指定:
  --col-mapping "time=datetime,open=开盘价,high=最高价,low=最低价,close=收盘价,volume=成交量"
        """,
    )
    p.add_argument("--ours", required=True, help="TradingStudio 导出的 CSV 文件")
    p.add_argument("--ref", required=True, help="参考数据 CSV (文华/博易)")
    p.add_argument("--instrument", required=True, help="合约代码 (如 ag2612)，用于查找 tick_size")
    p.add_argument("--tolerance", type=float, default=1.0,
                   help="允许的 tick 偏差数 (默认 1)")
    p.add_argument("--vol-threshold", type=float, default=0.02,
                   help="成交量相对差异阈值 (默认 0.02 = 2%%)")
    p.add_argument("--time-tol", type=int, default=30,
                   help="时间对齐容差秒数 (默认 30)")
    p.add_argument("--col-mapping", default="",
                   help="参考数据列名映射，格式: 'time=X,open=X,high=X,low=X,close=X,volume=X'")
    p.add_argument("--time-offset", type=int, default=0,
                   help="参考数据时间偏移秒数 (如参考时间比我们早 1 秒: -1)")
    p.add_argument("--output", "-o", default="", help="详细比对结果 CSV 路径")
    p.add_argument("--top", type=int, default=20, help="显示差异最大的 N 条 (默认 20)")
    p.add_argument("--verbose", "-v", action="store_true", help="显示详细匹配/不匹配列表")
    return p.parse_args()


def resolve_col_mapping(raw: str) -> dict:
    """解析列名映射字符串"""
    mapping = {
        "time": "bar_time",
        "open": "open",
        "high": "high",
        "low": "low",
        "close": "close",
        "volume": "volume",
    }
    if not raw:
        return mapping
    for pair in raw.split(","):
        pair = pair.strip()
        if "=" not in pair:
            continue
        k, v = pair.split("=", 1)
        k = k.strip()
        v = v.strip()
        if k in mapping:
            mapping[k] = v
    return mapping


def parse_bar_time(val: str) -> Optional[datetime]:
    """解析 bar_time 字符串，支持多种格式"""
    formats = [
        "%Y-%m-%d %H:%M:%S",
        "%Y-%m-%d %H:%M",
        "%Y/%m/%d %H:%M:%S",
        "%Y/%m/%d %H:%M",
        "%Y-%m-%d",
        "%Y/%m/%d",
    ]
    val = val.strip()
    for fmt in formats:
        try:
            return datetime.strptime(val, fmt)
        except ValueError:
            continue
    # 尝试 ISO 格式
    try:
        return datetime.fromisoformat(val)
    except ValueError:
        pass
    return None


def load_csv(path: str, col_mapping: dict, ours: bool = False) -> tuple[list[dict], list[str]]:
    """加载 CSV，返回 (行列表, 列名列表)"""
    rows = []
    with open(path, "r", encoding="utf-8-sig") as f:
        reader = csv.DictReader(f)
        fieldnames = reader.fieldnames or []

        for row in reader:
            parsed = {}
            # 解析时间
            time_key = "bar_time" if ours else col_mapping.get("time", "bar_time")
            time_str = row.get(time_key, "")
            bar_time = parse_bar_time(time_str)
            if bar_time is None:
                print(f"  [WARN] 跳过无法解析时间的行: {time_str}")
                continue
            parsed["bar_time"] = bar_time

            # 解析 OHLCV
            for field, ref_name in [("open", "open"), ("high", "high"),
                                     ("low", "low"), ("close", "close"),
                                     ("volume", "volume")]:
                col_name = field if ours else col_mapping.get(field, field)
                val = row.get(col_name, "")
                try:
                    parsed[field] = float(val.replace(",", "")) if val else 0.0
                except ValueError:
                    parsed[field] = 0.0
                    print(f"  [WARN] 无法解析 {field}={val} at {time_str}")

            # 可选的额外字段
            for extra in ["turnover", "open_interest", "trading_day", "instrument_id"]:
                if extra in row:
                    parsed[extra] = row[extra]

            rows.append(parsed)

    return rows, fieldnames


def align_bars(ours: list[dict], ref: list[dict], time_tol_sec: int, time_offset: int) -> tuple[list, list, list]:
    """
    按 bar_time 对齐两个数据集。如果双方都有 instrument_id，则按 (instrument_id, bar_time) 对齐。
    返回: (matched_pairs, ours_only, ref_only)
    """
    # 对参考数据应用时间偏移
    ref_adjusted = []
    for r in ref:
        r_adj = dict(r)
        r_adj["bar_time"] = r["bar_time"] + timedelta(seconds=time_offset)
        ref_adjusted.append(r_adj)

    # 检测是否双方都有 instrument_id（用于精确匹配多合约数据）
    ours_has_inst = any("instrument_id" in o for o in ours)
    ref_has_inst = any("instrument_id" in r for r in ref_adjusted)
    use_inst_match = ours_has_inst and ref_has_inst

    # 构建时间索引: key = (instrument_id, bar_time) 或 bar_time
    def make_key(row):
        t = row["bar_time"]
        inst = row.get("instrument_id", "")
        return (inst, t) if use_inst_match else t

    ours_by_key = {}
    for o in ours:
        k = make_key(o)
        if k not in ours_by_key:
            ours_by_key[k] = []
        ours_by_key[k].append(o)

    ref_by_key = {}
    for r in ref_adjusted:
        k = make_key(r)
        if k not in ref_by_key:
            ref_by_key[k] = []
        ref_by_key[k].append(r)

    all_keys = sorted(set(list(ours_by_key.keys()) + list(ref_by_key.keys())))

    matched = []
    ours_only = []
    ref_only = []

    used_ref_keys = set()

    for k in all_keys:
        if k in ours_by_key and k in ref_by_key:
            for o in ours_by_key[k]:
                for r in ref_by_key[k]:
                    matched.append((o, r, o["bar_time"]))
            used_ref_keys.add(k)
        elif k in ours_by_key and k not in ref_by_key:
            # 尝试模糊匹配（同 instrument_id 不同 bar_time）
            if use_inst_match:
                inst, t = k
                best_ref_k = None
                for rk in ref_by_key:
                    if rk in used_ref_keys:
                        continue
                    r_inst, r_t = rk if use_inst_match else ("", rk)
                    if r_inst == inst and abs((r_t - t).total_seconds()) <= time_tol_sec:
                        if best_ref_k is None or abs((rk[1] - t).total_seconds()) < abs((best_ref_k[1] - t).total_seconds()):
                            best_ref_k = rk
                if best_ref_k is not None:
                    for o in ours_by_key[k]:
                        for r in ref_by_key[best_ref_k]:
                            matched.append((o, r, o["bar_time"]))
                    used_ref_keys.add(best_ref_k)
                    continue
            for o in ours_by_key[k]:
                ours_only.append((o, o["bar_time"]))
        elif k in ref_by_key and k not in ours_by_key:
            for r in ref_by_key[k]:
                ref_only.append((r, r["bar_time"]))

    return matched, ours_only, ref_only


def compare_bars(matched: list, tick_size: float, tolerance: float,
                 vol_threshold: float) -> list[dict]:
    """逐 Bar 比较 OHLCV"""
    results = []
    for ours, ref, aligned_time in matched:
        row = {"bar_time": aligned_time.strftime("%Y-%m-%d %H:%M:%S")}

        # OHLC 比较（基于 tick）
        for field in ["open", "high", "low", "close"]:
            ours_val = ours.get(field, 0)
            ref_val = ref.get(field, 0)
            diff = ours_val - ref_val
            diff_ticks = abs(diff) / tick_size if tick_size > 0 else abs(diff)
            row[f"{field}_ours"] = ours_val
            row[f"{field}_ref"] = ref_val
            row[f"{field}_diff"] = round(diff, 6)
            row[f"{field}_ticks"] = round(diff_ticks, 2)
            row[f"{field}_bad"] = diff_ticks > tolerance

        # Volume 比较（相对比例）
        ours_vol = ours.get("volume", 0)
        ref_vol = ref.get("volume", 0)
        max_vol = max(abs(ours_vol), abs(ref_vol))
        vol_diff_pct = abs(ours_vol - ref_vol) / max_vol if max_vol > 0 else 0
        row["volume_ours"] = ours_vol
        row["volume_ref"] = ref_vol
        row["volume_diff"] = round(ours_vol - ref_vol, 2)
        row["volume_pct"] = round(vol_diff_pct * 100, 2)
        row["volume_bad"] = vol_diff_pct > vol_threshold

        results.append(row)

    return results


def print_summary(results: list, ours_only: list, ref_only: list,
                  instrument: str, tick_size: float, args):
    """打印比对摘要"""
    print()
    print("═" * 56)
    print(f"  交叉验证报告: {instrument}")
    print("═" * 56)
    print(f"  我方数据:    {args.ours}")
    print(f"  参考数据:    {args.ref}")
    print(f"  Tick Size:   {tick_size}")
    print(f"  容差:        {args.tolerance} tick(s) = {args.tolerance * tick_size} 元")
    print()

    n_matched = len(results)
    n_ours_only = len(ours_only)
    n_ref_only = len(ref_only)

    print(f"  ── 时间对齐 ──")
    print(f"  匹配 Bar:    {n_matched}")
    print(f"  仅我方:      {n_ours_only}")
    print(f"  仅参考:      {n_ref_only}")
    print()

    # OHLC 统计
    print(f"  ── OHLC 差异 (> {args.tolerance} tick{'s' if args.tolerance > 1 else ''}) ──")
    for field in ["open", "high", "low", "close"]:
        bad = [r for r in results if r.get(f"{field}_bad")]
        n_bad = len(bad)
        diffs = [r[f"{field}_ticks"] for r in results]
        avg_diff = sum(diffs) / len(diffs) if diffs else 0
        max_diff = max(diffs) if diffs else 0

        status = "PASS" if n_bad == 0 else ("WARN" if n_bad <= max(5, n_matched * 0.01) else "FAIL")
        print(f"  [{status}] {field.upper():6s}: {n_bad}/{n_matched} 超出容差  "
              f"(avg={avg_diff:.2f}t, max={max_diff:.2f}t)")

    # Volume 统计
    vol_bad = [r for r in results if r.get("volume_bad")]
    n_vol_bad = len(vol_bad)
    vol_pcts = [r["volume_pct"] for r in results]
    avg_vol_pct = sum(vol_pcts) / len(vol_pcts) if vol_pcts else 0
    max_vol_pct = max(vol_pcts) if vol_pcts else 0

    vol_status = "PASS" if n_vol_bad == 0 else ("WARN" if n_vol_bad <= max(10, n_matched * 0.05) else "FAIL")
    print(f"  [{vol_status}] VOLUME: {n_vol_bad}/{n_matched} 超出 {args.vol_threshold*100:.0f}%  "
          f"(avg={avg_vol_pct:.2f}%, max={max_vol_pct:.2f}%)")
    print()

    # Top N 差异
    all_bad = [r for r in results if any(
        r.get(f"{f}_bad") for f in ["open", "high", "low", "close"]
    )]
    # 按 max ticks 排序
    all_bad.sort(key=lambda r: max(
        r.get(f"{f}_ticks", 0) for f in ["open", "high", "low", "close"]
    ), reverse=True)

    if all_bad:
        top = all_bad[:args.top]
        print(f"  ── Top {len(top)} OHLC 差异 ──")
        print(f"  {'Time':<20} {'Field':<6} {'Ours':>10} {'Ref':>10} {'Diff':>10} {'Ticks':>7}")
        print(f"  {'-'*20} {'-'*6} {'-'*10} {'-'*10} {'-'*10} {'-'*7}")
        for r in top:
            for field in ["open", "high", "low", "close"]:
                if r.get(f"{field}_bad"):
                    print(f"  {r['bar_time']:<20} {field.upper():<6} "
                          f"{r[f'{field}_ours']:>10.4f} {r[f'{field}_ref']:>10.4f} "
                          f"{r[f'{field}_diff']:>10.4f} {r[f'{field}_ticks']:>7.2f}t")
    else:
        print(f"  [PASS] 所有 OHLC 均在 {args.tolerance} tick 容差内")
    print()

    # 未对齐的 Bar
    if ours_only:
        print(f"  ── 仅在我方存在的 Bar ({n_ours_only}) ──")
        for o, t in ours_only[:5]:
            print(f"    {t.strftime('%Y-%m-%d %H:%M:%S')}")
        if n_ours_only > 5:
            print(f"    ... 还有 {n_ours_only - 5} 条")
        print()

    if ref_only:
        print(f"  ── 仅在参考存在的 Bar ({n_ref_only}) ──")
        for r, t in ref_only[:5]:
            print(f"    {t.strftime('%Y-%m-%d %H:%M:%S')}")
        if n_ref_only > 5:
            print(f"    ... 还有 {n_ref_only - 5} 条")
        print()

    # 总体评级
    ohlc_bad_total = sum(
        1 for r in results
        if any(r.get(f"{f}_bad") for f in ["open", "high", "low", "close"])
    )
    ohlc_pct = ohlc_bad_total / n_matched * 100 if n_matched else 0

    if ohlc_pct < 0.5 and n_vol_bad / max(n_matched, 1) < 0.05 and n_ours_only / max(n_matched + n_ours_only, 1) < 0.02:
        rating = "PASS [OK]"
    elif ohlc_pct < 2.0:
        rating = "WARN [!]"
    else:
        rating = "FAIL [X]"

    print(f"  ── 总体评级: {rating} ──")
    print(f"  OHLC 异常率: {ohlc_pct:.2f}%  |  Vol 异常率: {n_vol_bad/max(n_matched,1)*100:.2f}%")
    print("═" * 56)


def write_report(results: list, ours_only: list, ref_only: list, output_path: str):
    """写入详细比对结果 CSV"""
    if not output_path:
        return

    fieldnames = [
        "bar_time", "status",
        "open_ours", "open_ref", "open_diff", "open_ticks",
        "high_ours", "high_ref", "high_diff", "high_ticks",
        "low_ours", "low_ref", "low_diff", "low_ticks",
        "close_ours", "close_ref", "close_diff", "close_ticks",
        "volume_ours", "volume_ref", "volume_diff", "volume_pct",
    ]

    with open(output_path, "w", newline="", encoding="utf-8-sig") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames, extrasaction="ignore")
        writer.writeheader()

        for r in results:
            is_bad = any(r.get(f"{f}_bad") for f in ["open", "high", "low", "close", "volume"])
            if is_bad:
                r["status"] = "MISMATCH"
            else:
                r["status"] = "OK"
            writer.writerow(r)

        # 追加 only-in-ours
        for o, t in ours_only:
            writer.writerow({
                "bar_time": t.strftime("%Y-%m-%d %H:%M:%S"),
                "status": "OURS_ONLY",
            })

        # 追加 only-in-ref
        for r, t in ref_only:
            writer.writerow({
                "bar_time": t.strftime("%Y-%m-%d %H:%M:%S"),
                "status": "REF_ONLY",
            })

    print(f"  详细报告: {output_path}")


def main():
    args = parse_args()

    tick_size = get_tick_size(args.instrument)
    col_mapping = resolve_col_mapping(args.col_mapping)

    print(f"\n加载我方数据: {args.ours}")
    ours_rows, _ = load_csv(args.ours, col_mapping, ours=True)
    print(f"  已加载 {len(ours_rows)} 行")

    print(f"加载参考数据: {args.ref}")
    ref_rows, _ = load_csv(args.ref, col_mapping, ours=False)
    print(f"  已加载 {len(ref_rows)} 行")

    if not ours_rows:
        print("[ERROR] 我方数据为空，请检查导出命令")
        sys.exit(1)
    if not ref_rows:
        print("[ERROR] 参考数据为空，请检查文件路径和列名映射")
        sys.exit(1)

    print(f"\n对齐 Bar 时间 (容差 ±{args.time_tol}s, 偏移 {args.time_offset}s)...")
    matched, ours_only, ref_only = align_bars(
        ours_rows, ref_rows, args.time_tol, args.time_offset
    )
    print(f"  匹配: {len(matched)}, 仅我方: {len(ours_only)}, 仅参考: {len(ref_only)}")

    print(f"\n逐 Bar 比较 (tolerance={args.tolerance} tick = {args.tolerance * tick_size} 元)...")
    results = compare_bars(matched, tick_size, args.tolerance, args.vol_threshold)

    print_summary(results, ours_only, ref_only, args.instrument, tick_size, args)

    if args.output:
        write_report(results, ours_only, ref_only, args.output)


if __name__ == "__main__":
    main()
