"""
批量回测 — 策略 × 品种基准绩效矩阵 (v2 — console output parse)
用法: python scripts/batch_backtest.py [--strategies S1,S2] [--instruments I1,I2]
"""
import json, subprocess, sys, time, os, re, argparse, io, tempfile
from pathlib import Path
from datetime import datetime

# Fix Windows GBK encoding
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
PROJECT = REPO_ROOT / "src" / "TradingStudio" / "TradingStudio.csproj"
HISTORY_DB = REPO_ROOT / "data" / "bars_history.duckdb"

DEFAULT_INSTRUMENTS = [
    "rb000", "hc000", "cu000", "al000", "zn000",
    "au000", "ag000", "ni000", "bu000", "ru000",
    "m000", "y000", "p000", "i000", "j000", "l000", "pp000", "v000",
    "MA000", "TA000", "FG000", "SA000", "SR000",
    "sc000", "lu000",
]

STRATEGY_TEMPLATES = {
    "MaCross": {
        "StrategyType": "MaCross", "PrimaryBarType": "bars_1min",
        "BarPeriodMinutes": 15, "AllocatedCapital": 1_000_000,
        "MaxDrawdownPct": 0.30, "MaxPositionPerInstrument": 2,
        "Priority": 1, "SessionFilter": "All", "SkipAuction": True,
        "Parameters": {"FastPeriod": 10, "SlowPeriod": 30,
            "AtrPeriod": 20, "StopAtrMult": 2.0,
            "RiskPerTrade": 0.02, "MaxMarginRatio": 0.25}
    },
    "SmaMacd": {
        "StrategyType": "SmaMacd", "PrimaryBarType": "bars_1min",
        "BarPeriodMinutes": 1, "AllocatedCapital": 1_000_000,
        "MaxDrawdownPct": 0.20, "MaxPositionPerInstrument": 50,
        "Priority": 1, "SessionFilter": "All", "SkipAuction": True,
        "Parameters": {"SmaPeriodsStr": "5,13,34,89,233",
            "MacdFast": 12, "MacdSlow": 26, "MacdSignal": 9,
            "MaxPositionRatio": 0.25, "StopLossPct": 0.02, "MaxLots": 20,
            "RequireSmaAlignment": True, "RequireMacdConfirm": True,
            "RequireDailyTrend": False, "Require1minConfirm": False}
    },
    "DonchianTrend": {
        "StrategyType": "DonchianTrend", "PrimaryBarType": "bars_1min",
        "BarPeriodMinutes": 15, "AllocatedCapital": 500_000,
        "MaxDrawdownPct": 0.25, "MaxPositionPerInstrument": 2,
        "Priority": 1, "SessionFilter": "All", "SkipAuction": True,
        "Parameters": {"ChannelPeriod": 20, "ExitPeriod": 10, "TrendMAPeriod": 50,
            "AtrPeriod": 20, "StopAtrMult": 2.0,
            "MinVolatility": 0.005, "RiskPerTrade": 0.02, "MaxBarsInTrade": 0}
    },
}


def parse_report(stdout: str) -> dict | None:
    """Parse console output of TradingStudio backtest to extract metrics."""
    try:
        result = {}

        # Final Equity: ¥944,628.55
        m = re.search(r'Final Equity:\s*[¥￥]\s*([\d,]+\.?\d*)', stdout)
        if m: result['finalEquity'] = float(m.group(1).replace(',', ''))

        # Total Return: -5.54%
        m = re.search(r'Total Return:\s*([+-]?[\d.]+)%', stdout)
        if m: result['return'] = float(m.group(1)) / 100

        # Max Drawdown: 7.05%
        m = re.search(r'Max Drawdown:\s*([\d.]+)%', stdout)
        if m: result['maxDD'] = float(m.group(1)) / 100

        # Strategy section
        # Net Profit: ¥-55,477.33
        m = re.search(r'Net Profit:\s*[¥￥]\s*([+-]?[\d,]+\.?\d*)', stdout)
        if m: result['netProfit'] = float(m.group(1).replace(',', ''))

        # Trades: 207
        m = re.search(r'Trades:\s*(\d+)', stdout)
        if m: result['trades'] = int(m.group(1))

        # Win Rate: 15.5%
        m = re.search(r'Win Rate:\s*([\d.]+)%', stdout)
        if m: result['winRate'] = float(m.group(1)) / 100

        # Avg Win: ¥218.76
        m = re.search(r'Avg Win:\s*[¥￥]\s*([\d,]+\.?\d*)', stdout)
        if m: result['avgWin'] = float(m.group(1).replace(',', ''))

        # Avg Loss: ¥-356.41
        m = re.search(r'Avg Loss:\s*[¥￥]\s*([+-]?[\d,]+\.?\d*)', stdout)
        if m: result['avgLoss'] = float(m.group(1).replace(',', ''))

        # Total Fees: ¥3,450.75
        m = re.search(r'Total Fees:\s*[¥￥]\s*([\d,]+\.?\d*)', stdout)
        if m: result['fees'] = float(m.group(1).replace(',', ''))

        # Total Slippage: 8320.00
        m = re.search(r'Total Slippage:\s*([\d.]+)', stdout)
        if m: result['slippage'] = float(m.group(1))

        if 'finalEquity' not in result:
            return None  # didn't parse successfully

        return result
    except Exception as e:
        return {"parse_error": str(e)}


def run_backtest(config_json: str, start_d: str, end_d: str) -> dict | None:
    """Write config to temp file, run backtest, parse stdout, return metrics."""
    # Write config to temp file
    with tempfile.NamedTemporaryFile(mode='w', suffix='.json',
                                     delete=False, encoding='utf-8') as f:
        f.write(config_json)
        cfg_path = f.name

    try:
        cmd = [
            "dotnet", "run", "--project", str(PROJECT),
            "--", "backtest",
            "--config", cfg_path,
            "--db", str(HISTORY_DB.resolve()),
            "--start", start_d, "--end", end_d,
        ]
        result = subprocess.run(
            cmd, capture_output=True, text=True,
            timeout=300, cwd=str(REPO_ROOT),
            encoding='utf-8', errors='replace',
        )

        if result.returncode != 0:
            stderr_tail = (result.stderr or "").strip().split("\n")[-5:]
            return {"error": "; ".join(stderr_tail), "rc": result.returncode}

        stdout = result.stdout or ""
        if not stdout.strip():
            return {"error": "empty stdout"}

        parsed = parse_report(stdout)
        if parsed is None:
            return {"error": "parse failed", "stdout_tail": stdout.strip()[-300:]}

        return parsed
    except subprocess.TimeoutExpired:
        return {"error": "timeout"}
    except Exception as e:
        return {"error": str(e)}
    finally:
        try: os.unlink(cfg_path)
        except: pass


def fmt_pct(v: float | None) -> str:
    if v is None: return "     N/A"
    return f"{v:7.1%}"

def fmt_float(v: float | None, w: int = 6) -> str:
    if v is None: return " N/A".rjust(w+1)
    return f"{v:{w}.2f}"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--strategies", type=str, default="MaCross,SmaMacd")
    parser.add_argument("--instruments", type=str)
    parser.add_argument("--start", type=str, default="2024-01-01")
    parser.add_argument("--end", type=str, default="2026-06-30")
    args = parser.parse_args()

    selected = [s.strip() for s in args.strategies.split(",")]
    strategies = {k: v for k, v in STRATEGY_TEMPLATES.items() if k in selected}
    instruments = [i.strip() for i in args.instruments.split(",")] if args.instruments else DEFAULT_INSTRUMENTS

    total = len(strategies) * len(instruments)
    print(f"BATCH BACKTEST: {len(strategies)} strategies x {len(instruments)} instruments = {total} runs")
    print(f"Date: {args.start} -> {args.end}  |  DB: {HISTORY_DB} ({HISTORY_DB.stat().st_size/1e9:.1f} GB)")
    print()

    results = {s: {} for s in strategies}
    start_time = time.time()
    completed = 0

    for s_idx, (sname, stemplate) in enumerate(strategies.items()):
        for i_idx, inst in enumerate(instruments):
            n = s_idx * len(instruments) + i_idx + 1
            inst_code = inst.replace("000", "")

            cfg = {
                "StrategyId": f"{sname}-{inst_code}",
                "Instruments": [inst],
                **stemplate,
            }
            cfg_json = json.dumps(cfg, indent=2, ensure_ascii=False)

            prefix = f"[{n:3d}/{total}] {sname:14s} x {inst:6s}"
            print(f"{prefix} ... ", end="", flush=True)
            t0 = time.time()
            r = run_backtest(cfg_json, args.start, args.end)
            dt = time.time() - t0

            results[sname][inst] = r

            if r and "error" not in r:
                ret = r.get("return") or 0
                icon = "[WIN]" if ret > 0.10 else ("[+]" if ret > 0 else ("[-]" if ret > -0.10 else "[--]"))
                eta = (time.time() - start_time) / max(completed or 1, 1) * (total - n)
                print(f"{icon} {fmt_pct(ret)} | DD{fmt_pct(r.get('maxDD'))} | {r.get('trades',0):4d}t | {dt:.0f}s | ETA {eta/60:.0f}m")
                completed += 1
            else:
                err_info = r.get("error", "?") if r else "None"
                # Truncate long errors
                if len(err_info) > 80:
                    err_info = err_info[:80] + "..."
                print(f"FAIL: {err_info}")

    total_time = time.time() - start_time

    # === MATRIX ===
    print()
    print("=" * 100)
    print(f"  BENCHMARK MATRIX — {total_time/60:.1f} min | {completed}/{total} ok")
    print("=" * 100)

    for sname in strategies:
        print(f"\n--- {sname} ---")
        print(f"{'Inst':>8s} | {'Return':>7s} | {'MaxDD':>7s} | {'Trades':>6s} | {'WinRate':>7s} | {'PnL':>12s}")
        print("-" * 70)
        for inst in instruments:
            r = results[sname].get(inst, {})
            if not r or "error" in r:
                print(f"{inst:>8s} | {'--':>7s}")
                continue
            print(f"{inst:>8s} | {fmt_pct(r.get('return'))} | {fmt_pct(r.get('maxDD'))} | {r.get('trades',0):6d} | {fmt_pct(r.get('winRate'))} | {r.get('netProfit') or 0:12,.0f}")

    # === RANKINGS ===
    print(f"\n=== STRATEGY RANKING (avg return across {len(instruments)} instruments) ===")
    rankings = []
    for sname in strategies:
        vals = [v.get("return") for v in results[sname].values() if v and "error" not in v and v.get("return") is not None]
        if vals:
            pos = sum(1 for v in vals if v > 0)
            rankings.append((sname, sum(vals)/len(vals), pos, len(vals)))
    rankings.sort(key=lambda x: x[1], reverse=True)
    for i, (name, avg, pos, n) in enumerate(rankings):
        print(f"  {i+1}. {name:14s}  avg {avg:+.1%}  win {pos}/{n}")

    print(f"\n=== TOP INSTRUMENTS (avg return across {len(strategies)} strategies) ===")
    inst_ranks = []
    for inst in instruments:
        vals = [results[s][inst].get("return") for s in strategies
                if results[s].get(inst) and "error" not in results[s][inst] and results[s][inst].get("return") is not None]
        if vals:
            pos = sum(1 for v in vals if v > 0)
            inst_ranks.append((inst, sum(vals)/len(vals), pos))
    inst_ranks.sort(key=lambda x: x[1], reverse=True)
    for i, (inst, avg, pos) in enumerate(inst_ranks[:10]):
        print(f"  {i+1:2d}. {inst:6s}  avg {avg:+.1%}  win {pos}/{len(strategies)}")

    # === SAVE ===
    summary_dir = REPO_ROOT / "configs" / "batch"
    summary_dir.mkdir(parents=True, exist_ok=True)
    summary_path = summary_dir / f"batch_summary_{datetime.now().strftime('%Y%m%d_%H%M%S')}.json"
    summary = {
        "generated": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "config": {"start": args.start, "end": args.end},
        "strategies": list(strategies.keys()),
        "instruments": instruments,
        "totalTime": total_time,
        "completed": completed,
        "results": results,
    }
    with open(summary_path, "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=2, ensure_ascii=False, default=str)
    print(f"\nSummary: {summary_path}")


if __name__ == "__main__":
    main()
