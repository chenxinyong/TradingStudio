"""GridSearch — MA 双均线参数优化 × RB 连续合约"""
import json, subprocess, sys, io, time, itertools
from pathlib import Path
from collections import defaultdict

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

REPO = Path('c:/Works/ClaudeCode/TradingStudio')
PROJECT = REPO / 'src' / 'TradingStudio' / 'TradingStudio.csproj'
CONTINUOUS = REPO / 'data' / 'continuous'
OUT_DIR = REPO / 'configs' / 'grid'
OUT_DIR.mkdir(parents=True, exist_ok=True)

# 参数网格（核心维度，控制在 30 组合以内）
FAST = [5, 10, 15, 20]
SLOW = [20, 30, 40, 60]
ATR_MULT = [2.0, 2.5]
MAX_POS = [2]
RISK_PCT = [0.02]

# 过滤无效组合 (fast >= slow)
combos = [(f, s, a, mp, r) for f, s, a, mp, r in
          itertools.product(FAST, SLOW, ATR_MULT, MAX_POS, RISK_PCT)
          if f < s]
# 加上默认参数（确保至少跑一次）
combos = list(set(combos))
print(f"Grid: FAST×{len(FAST)} SLOW×{len(SLOW)} ATR×{len(ATR_MULT)} Pos×{len(MAX_POS)} Risk×{len(RISK_PCT)}")
print(f"Total combos: {len(combos)}")

# 只跑 RB
INST = "rb"
results = []
start_time = time.time()

for i, (fast, slow, atr_mult, max_pos, risk_pct) in enumerate(combos):
    sid = f"GS-{INST}-F{fast}S{slow}A{int(atr_mult*10)}P{max_pos}R{int(risk_pct*1000)}"
    config = {
        "Parameters": {
            "FastPeriod": fast, "SlowPeriod": slow,
            "MaxMarginRatio": 0.25, "StopAtrMult": atr_mult,
            "AtrPeriod": 20, "RiskPerTrade": risk_pct,
            "MaxPosition": max_pos
        },
        "AllocatedCapital": 1_000_000, "MaxDrawdownPct": 0.3,
        "BarPeriodMinutes": 15,
        "Instruments": [INST], "SkipAuction": True,
        "MaxPositionPerInstrument": 5, "Priority": 1,
        "SessionFilter": "All", "PrimaryBarType": "bars_1min",
        "StrategyType": "MaCross", "StrategyId": sid
    }

    cp = OUT_DIR / f"{sid}.json"
    json.dump(config, open(str(cp), 'w'), indent=2)

    if i % 10 == 0:
        print(f"[{i+1}/{len(combos)}] Running F={fast} S={slow}...", end=" ", flush=True)

    try:
        r = subprocess.run([
            "dotnet", "run", "--project", str(PROJECT), "--",
            "backtest",
            "--config", str(cp.resolve()),
            "--continuous-dir", str(CONTINUOUS.resolve()),
            "--start", "2021-01-01", "--end", "2025-12-31"
        ], capture_output=True, text=True, timeout=120)

        if r.returncode == 0:
            rp = cp.with_suffix('.report.json')
            if rp.exists():
                with open(str(rp)) as f:
                    data = json.load(f)
                sr = data["strategyReports"][0]

                # 计算综合评分：利润因子、夏普近似
                trades = sr["totalTrades"]
                wr = sr["winRate"]
                pnl = sr["totalNetProfit"]
                dd = sr.get("maxDrawdown", 1.0)
                eq = sr["finalEquity"]

                # 简化夏普：平均收益/收益标准差 近似
                total_return = (eq - 1_000_000) / 1_000_000

                results.append({
                    "fast": fast, "slow": slow, "atr": atr_mult,
                    "pos": max_pos, "risk": risk_pct,
                    "trades": trades, "winRate": wr, "pnl": pnl,
                    "maxDD": dd, "finalEq": eq, "return": total_return,
                    "combo": f"F{fast}S{slow}A{atr_mult}P{max_pos}R{risk_pct:.3f}"
                })

                if i % 10 == 0:
                    print(f"✓ T={trades} PnL={pnl:+,.0f} DD={dd:.1%}")
            else:
                if i % 10 == 0: print("✗ no report")
        else:
            if i % 10 == 0: print("✗ FAIL")
    except subprocess.TimeoutExpired:
        if i % 10 == 0: print("✗ TIMEOUT")
    except Exception as e:
        if i % 10 == 0: print(f"✗ {e}")

elapsed = time.time() - start_time
print(f"\n{'='*80}")
print(f"GridSearch complete: {len(results)}/{len(combos)} succeeded in {elapsed:.0f}s")
print(f"{'='*80}")

if not results:
    print("No results!")
    sys.exit(1)

# Sort by total return (or could use Sharpe)
results.sort(key=lambda x: x["return"], reverse=True)

# Top 10
print(f"\n{'Rank':<5} {'Params':<25} {'Trades':>6} {'Win%':>6} {'PnL':>12} {'MaxDD':>7} {'Return':>8}")
print("-" * 76)
for i, r in enumerate(results[:10]):
    print(f"{i+1:<5} {r['combo']:<25} {r['trades']:>6} {r['winRate']:>5.0%} {r['pnl']:>12,.0f} {r['maxDD']:>6.1%} {r['return']:>7.1%}")

# Bottom 5
print(f"...")
for i, r in enumerate(results[-5:]):
    print(f"{len(results)-5+i+1:<5} {r['combo']:<25} {r['trades']:>6} {r['winRate']:>5.0%} {r['pnl']:>12,.0f} {r['maxDD']:>6.1%} {r['return']:>7.1%}")

# Best by metric
print(f"\n--- Best by Metric ---")
best_pnl = max(results, key=lambda x: x["pnl"])
best_wr = max(results, key=lambda x: x["winRate"])
best_dd = min(results, key=lambda x: x["maxDD"])
print(f"  Best PnL:     {best_pnl['combo']}  PnL={best_pnl['pnl']:,.0f}")
print(f"  Best WinRate: {best_wr['combo']}  WR={best_wr['winRate']:.1%}")
print(f"  Best MaxDD:   {best_dd['combo']}  DD={best_dd['maxDD']:.1%}")

# Parameter sensitivity
print(f"\n--- Parameter Sensitivity (Avg Return by Value) ---")
for param, key in [("FastPeriod", "fast"), ("SlowPeriod", "slow"), ("ATR Mult", "atr")]:
    by_val = defaultdict(list)
    for r in results: by_val[r[key]].append(r["return"])
    print(f"  {param}: ", end="")
    for val in sorted(by_val):
        avg = sum(by_val[val]) / len(by_val[val])
        print(f"{val}={avg:.1%}  ", end="")
    print()

# Save full results
summary_path = OUT_DIR / "grid_summary.json"
json.dump({"generated": time.strftime("%Y-%m-%d %H:%M:%S"), "total": len(results), "results": results},
          open(str(summary_path), 'w'), indent=2, ensure_ascii=False)
print(f"\nSaved: {summary_path}")
