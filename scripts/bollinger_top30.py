"""
布林带Top30全品种批量回测
用法: python scripts/bollinger_top30.py [--run] [--parallel 3]
"""
import json, os, sys, subprocess, re
from pathlib import Path
from datetime import datetime
from concurrent.futures import ProcessPoolExecutor, as_completed

BASE = Path(__file__).parent.parent
PROJECT = BASE / "src" / "TradingStudio"
DB = BASE / "data" / "bars_history.duckdb"
CONFIG_DIR = BASE / "configs" / "bollinger_top30"
START, END = "2021-01-01", "2026-06-30"

PRODUCTS = [
    "rb000","ag000","sa000","fg000","ru000","ma000","ta000","v000",
    "i000","j000","jm000","hc000","bu000","fu000","sc000","lu000",
    "sp000","eg000","eb000","pp000","l000","pg000","si000","lc000",
    "ec000","ao000","br000","cu000","al000","zn000","ni000","sn000",
    "pb000","ss000","au000","m000","y000","p000","a000","b000",
    "c000","cs000","jd000","lh000","cf000","sr000","oi000","rm000",
    "cy000","cj000","pk000","ap000","sm000","sf000",
]

TEMPLATE = {
    "StrategyType": "BollingerReversion",
    "PrimaryBarType": "bars_1min", "BarPeriodMinutes": 15,
    "AllocatedCapital": 1000000, "MaxDrawdownPct": 0.30,
    "MaxPositionPerInstrument": 2, "Parameters": {
        "Period": 20, "StdDevMult": 2.0,
        "StopLossPct": 0.015, "TakeProfitBand": 1.0,
        "AtrPeriod": 14, "TrailAtrMult": 1.5,
        "RiskPerTrade": 0.015, "MaxPosition": 2,
        "MaxAdx": 0, "AdxPeriod": 14,
        "MinBandWidth": 0.01, "EntryCooldown": 5, "EntryZoneRatio": 0.25,
    }
}

def generate():
    CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    for inst in PRODUCTS:
        code = inst.replace("000","").upper()
        cfg = dict(TEMPLATE)
        cfg["StrategyId"] = f"BB-{code}-15min"
        cfg["Instruments"] = [inst]
        path = CONFIG_DIR / f"bb_{code}_15min.json"
        with open(path, "w") as f:
            json.dump(cfg, f, indent=2)
    print(f"生成 {len(PRODUCTS)} 个配置 → {CONFIG_DIR}")

def run_one(path):
    cmd = ["dotnet","run","--project",str(PROJECT),"--no-build","--",
           "backtest","--config",str(path),"--db",str(DB),"--start",START,"--end",END]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=600)
    out = r.stdout + r.stderr
    m = re.search(r"Net Profit[:\s]+([-\d,.]+)", out)
    pnl = float(m.group(1).replace(",","")) if m else 0
    m = re.search(r"WinRate[:\s]+([\d.]+)%", out)
    wr = float(m.group(1))/100 if m else 0
    m = re.search(r"Max Drawdown[:\s]+([\d.]+)%", out)
    dd = float(m.group(1))/100 if m else 0
    report = Path(str(path).replace(".json",".report.json"))
    trades = 0
    if report.exists():
        with open(report) as f:
            sr = json.load(f)["strategyReports"][0]
        trades = sr.get("totalTrades", 0)
        if trades > 0:
            wins = [t for t in sr["trades"] if t["pnL"] > 0]
            losses = [t for t in sr["trades"] if t["pnL"] <= 0]
            aw = sum(t["pnL"] for t in wins)/len(wins) if wins else 0
            al = sum(t["pnL"] for t in losses)/len(losses) if losses else 0
            plr = abs(aw/al) if al else 0
        else:
            plr = 0
    else:
        plr = 0
    code = path.stem.split("_")[1]
    return {"code":code,"trades":trades,"pnl":pnl,"wr":wr,"dd":dd,"plr":plr}

def run_batch(parallel=2):
    paths = sorted(CONFIG_DIR.glob("bb_*.json"))
    results = []
    with ProcessPoolExecutor(max_workers=parallel) as ex:
        futures = {ex.submit(run_one, p): p for p in paths}
        for i, f in enumerate(as_completed(futures)):
            r = f.result()
            results.append(r)
            print(f"[{i+1:>2}/{len(paths)}] {r['code']:<6} {r['trades']:>5}t PnL={r['pnl']:>12,.0f} WR={r['wr']*100:>4.0f}% PLR={r['plr']:.2f} DD={r['dd']*100:>5.1f}%")

    results.sort(key=lambda x: x["pnl"], reverse=True)
    profitable = [r for r in results if r["pnl"] > 0]
    total = sum(r["pnl"] for r in results)
    print(f"\n盈利: {len(profitable)}/{len(results)}  合计PnL: {total:,.0f}")
    print(f"\nTop 10:")
    for r in results[:10]:
        print(f"  {r['code']:<6} {r['trades']:>5}t PnL={r['pnl']:>12,.0f} PLR={r['plr']:.2f}")

    ts = datetime.now().strftime("%Y%m%d_%H%M")
    with open(CONFIG_DIR / f"_summary_{ts}.json", "w") as f:
        json.dump(results, f, indent=2)

if __name__ == "__main__":
    if "--run" in sys.argv:
        p = 3
        for a in sys.argv:
            if a.startswith("--parallel="): p = int(a.split("=")[1])
        run_batch(p)
    else:
        generate()
        print(f"\n执行回测: python scripts/bollinger_top30.py --run [--parallel=3]")
