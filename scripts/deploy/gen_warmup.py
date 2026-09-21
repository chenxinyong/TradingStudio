#!/usr/bin/env python
"""生成云端 Live 模式小体积预热库 bars_warmup.duckdb

从历史库 (bars_history.duckdb) + 本地实盘库 (bars_live.duckdb) 提取
live 策略品种的 bars_1min，合并去重后写入小库，随部署包发布。

云端引擎 WarmupDays=120 → 读取 Today-240 天起的 1min bar 预热指标，
无需上传 28GB 历史大库。

用法: python scripts/deploy/gen_warmup.py
"""
import os
import sys
import duckdb

HISTORY_DB = r"C:\Works\Datas\bars_history.duckdb"
LIVE_DB = r"src\TradingStudio\data\bars_live.duckdb"
TARGET_DB = r"src\TradingStudio\data\bars_warmup.duckdb"

# 与 strategies/live/*.json 保持同步
INSTRUMENTS = ["ag2612", "sa701", "ta701", "v2701"]

def main() -> None:
    root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    history = HISTORY_DB if os.path.isabs(HISTORY_DB) else os.path.join(root, HISTORY_DB)
    live = LIVE_DB if os.path.isabs(LIVE_DB) else os.path.join(root, LIVE_DB)
    target = TARGET_DB if os.path.isabs(TARGET_DB) else os.path.join(root, TARGET_DB)

    ids = ",".join(f"'{i}'" for i in INSTRUMENTS)
    where = f"LOWER(instrument_id) IN ({ids})"

    src_blocks = []
    for name, path in (("hist", history), ("live", live)):
        if not os.path.exists(path):
            print(f"[warn] {name} 数据源不存在，跳过: {path}")
            continue
        src_blocks.append(f"(SELECT * FROM {name}.bars_1min WHERE {where})")

    if not src_blocks:
        print("[error] 没有任何数据源可用")
        sys.exit(1)

    if os.path.exists(target):
        os.remove(target)

    con = duckdb.connect(target)
    con.execute(f"ATTACH '{history.replace(chr(39), chr(39)*2)}' AS hist (READ_ONLY)")
    con.execute(f"ATTACH '{live.replace(chr(39), chr(39)*2)}' AS live (READ_ONLY)")
    sql = f"""
        CREATE TABLE bars_1min AS
        SELECT * FROM (
            {' UNION ALL '.join(src_blocks)}
        ) t
        QUALIFY ROW_NUMBER() OVER (PARTITION BY instrument_id, bar_time
                                   ORDER BY trading_day DESC) = 1
        ORDER BY instrument_id, bar_time
    """
    con.execute(sql)

    rows = con.execute(
        "SELECT instrument_id, COUNT(*), MIN(bar_time)::VARCHAR, MAX(bar_time)::VARCHAR "
        "FROM bars_1min GROUP BY instrument_id ORDER BY instrument_id").fetchall()
    con.close()

    print(f"[ok] 已生成 {target}")
    print(f"     大小: {os.path.getsize(target) / 1024 / 1024:.2f} MB")
    for r in rows:
        print(f"     {r[0]}: {r[1]} bars  [{r[2]} ~ {r[3]}]")

if __name__ == "__main__":
    main()
