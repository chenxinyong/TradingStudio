"""
品种独立参数优化 — 5品种 × MaCross (Fast/Slow/TakeProfit) grid search
用法: python scripts/perf_grid.py [--run] [--varieties RB,AG,SA,FG,RU]
      不加 --run 只生成配置文件, 加 --run 执行回测
"""
import itertools, json, os, sys, subprocess, re
from pathlib import Path
from concurrent.futures import ProcessPoolExecutor, as_completed
from datetime import datetime

BASE_DIR = Path(__file__).parent.parent.parent
CONFIG_DIR = BASE_DIR / "configs" / "perf"
PROJECT = BASE_DIR / "src" / "TradingStudio"
DB = BASE_DIR / "data" / "bars_history.duckdb"
START = "2021-01-01"
END = "2026-06-30"

# 5大品种 × 参数网格
VARIETIES = {
    "RB": "rb000", "AG": "ag000", "SA": "sa000", "FG": "fg000", "RU": "ru000"
}

# 每个品种独立参数范围
PARAM_GRIDS = {
    # (FastPeriod, SlowPeriod, TakeProfitAtrMult)
    # TakeProfitAtrMult=0 为禁用止盈（对照组）
    "base": [
        (10, 20, 0), (10, 30, 0), (10, 20, 2.0),
        (10, 20, 2.5), (10, 30, 2.0), (10, 30, 2.5),
        (15, 30, 2.5), (15, 40, 3.0), (20, 60, 3.0),
    ],
    # 针对特定品种可覆盖
}

def generate_configs(varieties: list[str] = None, dry_run: bool = True):
    """生成回测配置 JSON 文件"""
    if varieties is None:
        varieties = list(VARIETIES.keys())

    CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    configs = []

    for var in varieties:
        inst = VARIETIES[var]
        grid = PARAM_GRIDS.get(var.lower(), PARAM_GRIDS["base"])

        for fast, slow, tp in grid:
            tp_label = f"TP{tp:.1f}" if tp > 0 else "noTP"
            sid = f"GS-{var}-F{fast}S{slow}-{tp_label}"
            cfg = {
                "StrategyId": sid,
                "StrategyType": "MaCross",
                "Description": f"{var} MA({fast}/{slow}) 15min TP={tp}",
                "Version": 1,
                "Instruments": [inst],
                "PrimaryBarType": "bars_1min",
                "BarPeriodMinutes": 15,
                "AllocatedCapital": 1000000,
                "MaxDrawdownPct": 0.30,
                "MaxPositionPerInstrument": 2,
                "Priority": 1,
                "SessionFilter": "All",
                "SkipAuction": True,
                "Parameters": {
                    "FastPeriod": fast,
                    "SlowPeriod": slow,
                    "AtrPeriod": 20,
                    "StopAtrMult": 2.0,
                    "TakeProfitAtrMult": tp,
                    "RiskPerTrade": 0.02,
                    "MaxMarginRatio": 0.25,
                },
            }

            path = CONFIG_DIR / f"{sid}.json"
            if not dry_run:
                with open(path, "w") as f:
                    json.dump(cfg, f, indent=2)
            configs.append((path, cfg))

    print(f"生成 {len(configs)} 个配置 ({len(varieties)}品种 × {len(PARAM_GRIDS['base'])}组合)")
    return configs


def run_single(config_path: Path) -> dict:
    """执行单个回测, 解析结果"""
    cmd = [
        "dotnet", "run", "--project", str(PROJECT),
        "--", "backtest",
        "--config", str(config_path),
        "--db", str(DB),
        "--start", START, "--end", END,
    ]
    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
        output = result.stdout + result.stderr

        # 解析关键指标
        m = re.search(r"TotalNetProfit[:\s]+([-\d,.]+)", output)
        pnl = float(m.group(1).replace(",", "")) if m else 0

        m = re.search(r"WinRate[:\s]+([\d.]+)%", output)
        win_rate = float(m.group(1)) if m else 0

        m = re.search(r"MaxDrawdown[:\s]+([\d.]+)%", output)
        dd = float(m.group(1)) if m else 0

        m = re.search(r"Sharpe[:\s]+([-\d.]+)", output)
        sharpe = float(m.group(1)) if m else 0

        # 读取 report.json
        report_path = Path(str(config_path).replace(".json", ".report.json"))
        trades = 0
        if report_path.exists():
            with open(report_path) as f:
                r = json.load(f)
            sr = r.get("strategyReports", [{}])[0]
            trades = sr.get("totalTrades", 0)

        return {
            "config": config_path.stem, "pnl": pnl, "win_rate": win_rate,
            "max_dd": dd, "sharpe": sharpe, "trades": trades,
        }
    except Exception as e:
        return {"config": config_path.stem, "pnl": 0, "error": str(e)}


def run_batch(parallel: int = 2):
    """批量执行回测"""
    configs = generate_configs(dry_run=False)
    paths = [c[0] for c in configs if c[0].exists()]

    print(f"\n开始回测 {len(paths)} 个配置 (并行={parallel})...")
    results = []

    with ProcessPoolExecutor(max_workers=parallel) as ex:
        futures = {ex.submit(run_single, p): p for p in paths}
        for i, f in enumerate(as_completed(futures)):
            r = f.result()
            results.append(r)
            print(f"  [{i+1}/{len(paths)}] {r['config']:<40} "
                  f"PnL={r.get('pnl',0):>12,.0f}  WR={r.get('win_rate',0):>5.1f}%  "
                  f"DD={r.get('max_dd',0):>5.1f}%  Sharpe={r.get('sharpe',0):>6.2f}  "
                  f"Trades={r.get('trades',0)}")

    # 排序输出
    results.sort(key=lambda x: x.get("pnl", 0), reverse=True)
    print(f"\n{'='*90}")
    print(f"排名 (按PnL):")
    for i, r in enumerate(results[:20]):
        print(f"  {i+1:>2}. {r['config']:<40} PnL={r.get('pnl',0):>12,.0f}  "
              f"WR={r.get('win_rate',0):>5.1f}%  DD={r.get('max_dd',0):>5.1f}%")

    # 按品种汇总最优
    print(f"\n{'='*90}")
    print(f"各品种最优参数:")
    by_var = {}
    for r in results:
        var = r["config"].split("-")[1]
        if var not in by_var or r.get("pnl", 0) > by_var[var].get("pnl", float("-inf")):
            by_var[var] = r
    for var, r in sorted(by_var.items()):
        print(f"  {var}: {r['config']}  PnL={r.get('pnl',0):,.0f}  WR={r.get('win_rate',0):.0f}%")

    # 汇总
    timestamp = datetime.now().strftime("%Y%m%d_%H%M")
    summary_path = CONFIG_DIR / f"_perf_grid_{timestamp}.json"
    with open(summary_path, "w") as f:
        json.dump(results, f, indent=2)
    print(f"\n结果保存: {summary_path}")


if __name__ == "__main__":
    if "--run" in sys.argv:
        varieties = None
        for arg in sys.argv:
            if arg.startswith("--varieties="):
                varieties = arg.split("=")[1].split(",")
        run_batch(parallel=2)
    else:
        print("Dry-run 模式 (只生成配置). 加 --run 执行回测.")
        generate_configs(dry_run=True)
        print(f"\n配置将输出到: {CONFIG_DIR}")
        print(f"执行回测: python scripts/perf_grid.py --run")
