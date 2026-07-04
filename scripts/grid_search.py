"""参数网格搜索 — SmaMacd × al000"""
import json, subprocess, sys, time, os, re, tempfile, io, itertools
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

REPO_ROOT = Path(__file__).resolve().parent.parent
PROJECT = REPO_ROOT / "src" / "TradingStudio" / "TradingStudio.csproj"
HISTORY_DB = REPO_ROOT / "data" / "bars_history.duckdb"

START_DATE = "2024-01-01"
END_DATE = "2026-06-30"

BASE = {
    "StrategyType": "SmaMacd", "PrimaryBarType": "bars_1min",
    "BarPeriodMinutes": 1, "AllocatedCapital": 1_000_000,
    "MaxDrawdownPct": 0.20, "MaxPositionPerInstrument": 50,
    "Priority": 1, "SessionFilter": "All", "SkipAuction": True,
}

BASE_PARAMS = {
    "SmaPeriodsStr": "5,13,34,89,233",
    "MacdFast": 12, "MacdSlow": 26, "MacdSignal": 9,
    "MaxPositionRatio": 0.25, "StopLossPct": 0.02, "MaxLots": 20,
    "RequireSmaAlignment": True, "RequireMacdConfirm": True,
    "RequireDailyTrend": False, "Require1minConfirm": False,
}

# 核心参数搜索空间
GRID = {
    "StopLossPct": [0.01, 0.015, 0.02, 0.025, 0.03, 0.04, 0.05],
    "RequireSmaAlignment": [True, False],
    "RequireMacdConfirm": [True, False],
    "RequireDailyTrend": [True, False],
}

SMA_COMBOS = [
    "5,13,34,89,233",
    "5,13,34,55,144",
    "8,21,55,144,233",
    "10,20,50,100,200",
    "5,20,60,120,250",
    "3,8,21,55,144",
]


def run(instrument: str, params: dict) -> dict | None:
    cfg = dict(BASE)
    cfg["Instruments"] = [instrument]
    cfg["StrategyId"] = f"gs-{instrument}"
    p = dict(BASE_PARAMS)
    p.update(params)
    cfg["Parameters"] = p

    with tempfile.NamedTemporaryFile(mode='w', suffix='.json',
                                     delete=False, encoding='utf-8') as f:
        json.dump(cfg, f, indent=2, ensure_ascii=False)
        cp = f.name

    try:
        r = subprocess.run(
            ["dotnet", "run", "--project", str(PROJECT), "--", "backtest",
             "--config", cp, "--db", str(HISTORY_DB.resolve()),
             "--start", START_DATE, "--end", END_DATE],
            capture_output=True, text=True, timeout=120,
            encoding='utf-8', errors='replace')
        if r.returncode != 0: return None
        s = r.stdout or ""
        out = {"params": params}
        for label, pat in [
            ("return", r'Total Return:\s*([+-]?[\d.]+)%'),
            ("maxDD", r'Max Drawdown:\s*([\d.]+)%'),
            ("trades", r'Trades:\s*(\d+)'),
            ("winRate", r'Win Rate:\s*([\d.]+)%'),
            ("netProfit", r'Net Profit:\s*[¥￥]\s*([+-]?[\d,]+\.?\d*)'),
        ]:
            m = re.search(pat, s)
            if m: out[label] = float(m.group(1).replace(',', ''))
        if "return" not in out: return None
        ret = out.get("return", 0)
        dd = out.get("maxDD", 0.01) or 0.01
        tr = out.get("trades", 0)
        # Score: 优先盈利+低回撤，惩罚过少交易
        out["score"] = (ret / dd) * min(1.0, tr / 20.0) * 100
        return out
    except: return None
    finally:
        try: os.unlink(cp)
        except: pass


def main():
    inst = "al000"
    print(f"Grid Search: SmaMacd x {inst} | {START_DATE} -> {END_DATE}")
    print(f"Baseline: ret=-1.5% DD=8.3% 44t score={-1.5/8.3*100*min(1,44/20):.1f}")
    print()

    # Phase 1: core grid
    keys = list(GRID.keys())
    vals = list(GRID.values())
    total = 1
    for v in vals: total *= len(v)
    print(f"Phase 1: {total} combos ({len(keys)} params)")

    results = []
    t0 = time.time()
    for i, combo in enumerate(itertools.product(*vals)):
        params = dict(zip(keys, combo))
        print(f"[{i+1:3d}/{total}] {params} ... ", end="", flush=True)
        tt = time.time()
        r = run(inst, params)
        dt = time.time() - tt
        if r:
            results.append(r)
            sc = r["score"]
            print(f"{'WIN' if sc>0 else '-'} ret={r['return']:+.1f}% DD={r['maxDD']:.1f}% "
                  f"tr={r['trades']:.0f} wr={r['winRate']:.0f}% sc={sc:+.1f} | {dt:.0f}s")
        else:
            print(f"FAIL | {dt:.0f}s")

    elapsed = time.time() - t0

    # Phase 2: SMA combos with top params
    results.sort(key=lambda x: x["score"], reverse=True)
    if results:
        best = results[0]["params"]
        print(f"\nPhase 2: SMA scan with best params {best}")
        for i, sma in enumerate(SMA_COMBOS):
            params = dict(best)
            params["SmaPeriodsStr"] = sma
            print(f"[{i+1}/{len(SMA_COMBOS)}] SMA={sma} ... ", end="", flush=True)
            tt = time.time()
            r = run(inst, params)
            dt = time.time() - tt
            if r:
                results.append(r)
                sc = r["score"]
                print(f"{'WIN' if sc>0 else '-'} ret={r['return']:+.1f}% DD={r['maxDD']:.1f}% "
                      f"tr={r['trades']:.0f} sc={sc:+.1f} | {dt:.0f}s")

    results.sort(key=lambda x: x["score"], reverse=True)

    print(f"\n{'='*100}")
    print(f"  TOP RESULTS — {elapsed/60:.1f}min phase1 + SMA scan | {len(results)} valid")
    print(f"{'='*100}")
    hdr = f"{'#':>3s} │ {'Score':>6s} │ {'Ret':>6s} │ {'DD':>5s} │ {'Tr':>4s} │ {'WR':>5s} │ {'Stop%':>6s} │ {'Align':>5s} │ {'MACD':>4s} │ {'DayTr':>5s} │ {'SMA periods'}"
    print(hdr)
    print("-" * 100)

    for i, r in enumerate(results[:25]):
        p = r["params"]
        sma = p.get("SmaPeriodsStr", "default")
        if len(sma) > 30: sma = sma[:30]
        print(f"{i+1:3d} │ {r.get('score',0):5.1f} │ {r.get('return',0):5.1f}% │ {r.get('maxDD',0):4.1f}% │ {r.get('trades',0):4.0f} │ {r.get('winRate',0):4.0f}% │ {p.get('StopLossPct',0):5.3f} │ {str(p.get('RequireSmaAlignment',''))[0]:>5s} │ {str(p.get('RequireMacdConfirm',''))[0]:>4s} │ {str(p.get('RequireDailyTrend',''))[0]:>5s} │ {sma}")

    # Save
    out = REPO_ROOT / "configs" / "batch" / f"grid_{inst}.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    with open(out, "w", encoding="utf-8") as f:
        json.dump({"instrument": inst, "baseline": {"return": -1.5, "dd": 8.3, "trades": 44},
                   "results": results[:50]}, f, indent=2, ensure_ascii=False, default=str)
    print(f"\nSaved: {out}")


if __name__ == "__main__":
    main()
