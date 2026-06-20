"""批量回测 — MaCross 策略 × 全部连续合约品种"""
import subprocess, json, sys, io, os, time
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

REPO = Path(__file__).parent.parent
CONTINUOUS_DIR = REPO / "data" / "continuous"
CONFIGS_DIR = REPO / "configs" / "batch"
PROJECT_DIR = REPO / "src" / "TradingStudio"

# Template config (will override instrument-specific fields)
TEMPLATE = {
    "Parameters": {
        "FastPeriod": 10, "SlowPeriod": 30,
        "MaxMarginRatio": 0.25, "StopAtrMult": 2,
        "AtrPeriod": 20, "RiskPerTrade": 0.02,
        "MaxPosition": 2
    },
    "AllocatedCapital": 1_000_000,
    "MaxDrawdownPct": 0.3,
    "BarPeriodMinutes": 15,
    "Instruments": [],  # filled per-run
    "SkipAuction": True,
    "MaxPositionPerInstrument": 5,
    "Priority": 1,
    "SessionFilter": "All",
    "PrimaryBarType": "bars_1min",
    "StrategyType": "MaCross"
}

CONFIGS_DIR.mkdir(parents=True, exist_ok=True)

# Discover continuous contracts
instruments = sorted([
    f.stem.replace("_continuous", "")
    for f in CONTINUOUS_DIR.glob("*_continuous.duckdb")
])
print(f"Found {len(instruments)} instruments: {', '.join(instruments)}")

results = []
start_time = time.time()

for i, inst in enumerate(instruments):
    strategy_id = f"MaCross-{inst.upper()}"
    config = dict(TEMPLATE)
    config["Instruments"] = [inst]
    config["StrategyId"] = strategy_id

    config_path = CONFIGS_DIR / f"batch_{inst}.json"
    json.dump(config, open(config_path, "w", encoding="utf-8"), indent=2, ensure_ascii=False)

    print(f"[{i+1}/{len(instruments)}] {inst} ...", end=" ", flush=True)

    try:
        result = subprocess.run(
            [
                "dotnet", "run", "--project", str(PROJECT_DIR / "TradingStudio.csproj"),
                "--",
                "backtest",
                "--config", str(config_path.resolve()),
                "--continuous-dir", str(CONTINUOUS_DIR.resolve()),
                "--start", "2021-01-01",
                "--end", "2025-12-31"
            ],
            capture_output=True, text=True, timeout=120
        )

        if result.returncode == 0:
            report_path = config_path.with_suffix(".report.json")
            if report_path.exists():
                with open(report_path) as f:
                    r = json.load(f)
                sr = r["strategyReports"][0]
                total_trades = sr["totalTrades"]
                win_rate = sr["winRate"]
                net_profit = sr["totalNetProfit"]
                max_dd = sr.get("maxDrawdown", 0)
                results.append({
                    "inst": inst,
                    "trades": total_trades,
                    "winRate": win_rate,
                    "pnl": net_profit,
                    "maxDD": max_dd,
                    "finalEquity": sr["finalEquity"]
                })
                print(f"✓ {total_trades:4d} trades  WR={win_rate:.0%}  PnL={net_profit:+,.0f}  MaxDD={max_dd:.1%}")
            else:
                print("✗ report not found")
        else:
            err = result.stderr.split('\n')[-3:] if result.stderr else ["unknown"]
            print(f"✗ FAILED: {'; '.join(err)}")
    except subprocess.TimeoutExpired:
        print("✗ TIMEOUT")
    except Exception as e:
        print(f"✗ ERROR: {e}")

elapsed = time.time() - start_time
print(f"\n{'='*80}")
print(f"Batch complete: {len(results)}/{len(instruments)} succeeded in {elapsed:.0f}s")
print(f"{'='*80}")

# Summary table
if results:
    results.sort(key=lambda x: x["pnl"], reverse=True)
    print(f"\n{'Inst':>6s}  {'Trades':>6s}  {'WinRate':>7s}  {'PnL':>12s}  {'MaxDD':>7s}  {'FinalEquity':>14s}")
    print("-" * 70)
    total_pnl = 0; total_trades = 0
    for r in results:
        print(f"{r['inst']:>6s}  {r['trades']:6d}  {r['winRate']:6.0%}  {r['pnl']:12,.0f}  {r['maxDD']:6.1%}  {r['finalEquity']:14,.0f}")
        total_pnl += r['pnl']
        total_trades += r['trades']
    print("-" * 70)
    print(f"{'TOTAL':>6s}  {total_trades:6d}  {'':>7s}  {total_pnl:12,.0f}")

    # Winners/Losers
    winners = [r for r in results if r["pnl"] > 0]
    losers = [r for r in results if r["pnl"] <= 0]
    print(f"\n  Winners: {len(winners)}/{len(results)}  ({len(winners)/len(results)*100:.0f}%)")
    print(f"  Avg Win: {sum(r['pnl'] for r in winners)/len(winners):,.0f}" if winners else "  No winners")
    print(f"  Avg Loss: {sum(r['pnl'] for r in losers)/len(losers):,.0f}" if losers else "  No losers")

    # Save results JSON
    summary_path = CONFIGS_DIR / "batch_summary.json"
    json.dump({"generated": time.strftime("%Y-%m-%d %H:%M:%S"), "results": results},
              open(summary_path, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
    print(f"\nSummary saved: {summary_path}")
