"""
Tick 历史回补 — 从金数源 RAR 提取 Top 30 品种 2020-2026 全量 Tick CSV。

用法: python scripts/tick_backfill.py [--from 202001] [--to 202605] [--dry-run]
"""
import subprocess, os, sys, time, glob, re, json, shutil
from pathlib import Path

# ─── 配置 ───
UNRAR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "tools", "UnRAR.exe"))
RAR_BASE = r"C:\Works\Datas\Jinshuyuan"
OUT_BASE = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "data", "tick"))
RAR_PASSWORD = "www.jinshuyuan.net"
DRY_RUN = "--dry-run" in sys.argv

# Top 30 → (RAR内部交易所目录, 输出交易所名)
TOP30_MAP = {
    "rb": ("sc", "SHFE"), "hc": ("sc", "SHFE"), "ag": ("sc", "SHFE"),
    "fu": ("sc", "SHFE"), "bu": ("sc", "SHFE"), "ru": ("sc", "SHFE"),
    "sp": ("sc", "SHFE"), "ni": ("sc", "SHFE"), "al": ("sc", "SHFE"),
    "zn": ("sc", "SHFE"),
    "m": ("dc", "DCE"), "p": ("dc", "DCE"), "v": ("dc", "DCE"),
    "i": ("dc", "DCE"), "y": ("dc", "DCE"), "c": ("dc", "DCE"),
    "l": ("dc", "DCE"), "jm": ("dc", "DCE"), "pp": ("dc", "DCE"),
    "eb": ("dc", "DCE"), "eg": ("dc", "DCE"),
    "CF": ("zc", "CZCE"), "SR": ("zc", "CZCE"), "TA": ("zc", "CZCE"),
    "MA": ("zc", "CZCE"), "FG": ("zc", "CZCE"), "SA": ("zc", "CZCE"),
    "SM": ("zc", "CZCE"), "RM": ("zc", "CZCE"), "OI": ("zc", "CZCE"),
}

# ─── B方案: 从连续合约换月日志加载目标合约 ───
def load_target_contracts():
    """读取 data/continuous/{v}_continuous.duckdb 的 rollover_log，
       返回 {variety_code: set(contract_ids)}"""
    import duckdb
    target = {}
    cont_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "data", "continuous"))
    for f in sorted(os.listdir(cont_dir)):
        if not f.endswith('.duckdb'): continue
        v = f.replace('_continuous.duckdb', '')
        conn = duckdb.connect(os.path.join(cont_dir, f))
        rows = conn.execute("SELECT DISTINCT old_dominant FROM rollover_log UNION SELECT DISTINCT new_dominant FROM rollover_log").fetchall()
        contracts = set(r[0] for r in rows if r[0])
        # 加当前主力
        curr = conn.execute('SELECT dominant_contract FROM continuous_day ORDER BY trading_day DESC LIMIT 1').fetchone()
        if curr and curr[0]: contracts.add(curr[0])
        conn.close()
        if contracts:
            target[v] = contracts
    return target

TARGET_CONTRACTS = None  # 延迟加载

# ─── 参数解析 ───
def parse_range():
    from_month = "202001"
    to_month = "202606"
    for i, a in enumerate(sys.argv):
        if a in ("--from", "-f") and i+1 < len(sys.argv):
            from_month = sys.argv[i+1]
        if a in ("--to", "-t") and i+1 < len(sys.argv):
            to_month = sys.argv[i+1]
    return from_month, to_month

def discover_rars(from_month, to_month):
    """发现所有 RAR 文件"""
    rars = []
    # 目录结构: FutAC_TickKZ_CTP_Daily_YYYY/FutAC_TickKZ_CTP_Daily_YYYYMM.rar
    for year_dir in sorted(os.listdir(RAR_BASE)):
        year_path = os.path.join(RAR_BASE, year_dir)
        if not os.path.isdir(year_path) or not year_dir.startswith("FutAC_TickKZ_CTP_Daily_"):
            continue
        for f in sorted(os.listdir(year_path)):
            if not f.endswith(".rar"):
                continue
            # 提取 YYYYMM
            m = re.search(r"(\d{6})", f)
            if not m:
                continue
            ym = m.group(1)
            if from_month <= ym <= to_month:
                rars.append((ym, os.path.join(year_path, f)))
    return sorted(rars, key=lambda x: x[0])

# ─── 核心逻辑 ───
def list_rar_files(rar_path, pattern):
    """列出 RAR 中匹配通配符的文件"""
    cmd = [UNRAR, "lb", rar_path, pattern]
    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=120,
                                encoding="utf-8", errors="replace")
        files = [l.strip() for l in result.stdout.strip().split("\n") if l.strip()]
        return files
    except Exception as e:
        print(f"\n  [WARN] List failed: {e}")
        return []

def extract_files(rar_path, listfile_path, dest_dir):
    """从 RAR 提取 listfile 中列出的文件到目标目录"""
    os.makedirs(dest_dir, exist_ok=True)
    cmd = [UNRAR, "x", "-o+", f"-p{RAR_PASSWORD}", rar_path, f"@{listfile_path}", dest_dir + "\\"]
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=300, encoding="gbk")
    return result.returncode == 0, result.stdout

def parse_contract_info(filename):
    """
    从文件名解析合约信息和日期
    例: sc/rb2005_20200102.csv → valid
        sc/rb主力连续_20200102.csv → None (过滤掉连续合约变体)
    """
    # RAR 内部路径用反斜杠（Windows）或正斜杠
    basename = filename.replace("\\", "/").rsplit("/", 1)[-1].replace(".csv", "")
    parts = basename.split("_")
    if len(parts) < 2:
        return None

    contract = parts[0]  # rb2005, TA005
    date_str = parts[1]  # 20200102

    if len(date_str) != 8:
        return None

    # 过滤：只保留标准合约代码（字母+数字），排除中文名称
    # 标准合约: rb2005, TA005, m2501
    # 非标准: rb主力连续, rb指数, rb次主力连续
    if not re.match(r"^[A-Za-z]+\d+$", contract):
        return None

    # 品种代码：提取字母部分
    variety = re.match(r"^([A-Za-z]+)", contract)
    if not variety:
        return None
    variety_code = variety.group(1).lower()

    # 交易所：从路径第一段
    exchange = filename.replace("\\", "/").split("/")[0]

    # 只在 Top 30 名单中的品种
    if variety_code not in TOP30_MAP:
        return None

    # 合约代码合法性：至少包含月份信息
    if len(contract) < 4:
        return None

    return {
        "exchange": exchange,
        "variety": variety_code,
        "contract": contract,
        "date": date_str,
        "year": date_str[:4],
        "full_path": filename,
    }

def organize_file(src_path, dest_base, info):
    """
    将提取出的文件移动到目标结构:
    data/tick/{EXCHANGE}/{variety}/{year}/{contract}_{date}.csv
    """
    if info is None:
        return False

    ex_full = {"sc": "SHFE", "dc": "DCE", "zc": "CZCE", "ine": "INE", "gfex": "GFEX"}
    ex_name = ex_full.get(info["exchange"], info["exchange"].upper())

    dest_dir = os.path.join(dest_base, ex_name, info["variety"], info["year"])
    os.makedirs(dest_dir, exist_ok=True)

    basename = os.path.basename(info["full_path"])
    dest_path = os.path.join(dest_dir, basename)

    if os.path.exists(dest_path):
        return False  # 已存在

    if os.path.exists(src_path):
        os.rename(src_path, dest_path)
        return True
    return False

# ─── 主流程 ───
def main():
    from_month, to_month = parse_range()
    print(f"Tick Backfill: {from_month} ~ {to_month}")
    print(f"Output: {os.path.abspath(OUT_BASE)}")
    print(f"Varieties: {len(TOP30_MAP)} (Top 30)")
    if DRY_RUN:
        print("*** DRY RUN MODE ***")
    print()

    # 加载方案B目标合约
    global TARGET_CONTRACTS
    TARGET_CONTRACTS = load_target_contracts()
    total_contracts = sum(len(v) for v in TARGET_CONTRACTS.values())
    print(f"Target contracts: {total_contracts} (from rollover_log)")
    print()

    rars = discover_rars(from_month, to_month)
    print(f"RAR files: {len(rars)}")
    print()

    # 进度文件
    progress_file = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "data", "tick_backfill_progress.txt"))
    os.makedirs(os.path.dirname(progress_file), exist_ok=True)
    completed = set()
    if os.path.exists(progress_file):
        with open(progress_file) as f:
            completed = set(l.strip() for l in f if l.strip())

    total_extracted = 0
    total_organized = 0
    t_start = time.time()
    os.makedirs(OUT_BASE, exist_ok=True)  # 确保输出目录存在

    for idx, (ym, rar_path) in enumerate(rars):
        if ym in completed:
            print(f"[{idx+1}/{len(rars)}] {ym} — already done, skip")
            continue

        t0 = time.time()
        print(f"[{idx+1}/{len(rars)}] {ym}  {os.path.basename(rar_path)}", end=" ", flush=True)

        # 1. 列出全部文件，Python 侧按目标合约过滤（B方案：仅换月日志中的主力合约）
        all_raw = list_rar_files(rar_path, "*")
        all_files = []
        target_set = set()
        for variety, contracts in TARGET_CONTRACTS.items():
            for c in contracts:
                target_set.add(c.lower())  # 统一小写比较

        for f in all_raw:
            basename = f.replace("\\", "/").rsplit("/", 1)[-1]
            contract = basename.split("_")[0].lower()
            if contract in target_set:
                all_files.append(f)

        if not all_files:
            print(f"— no Top 30 files")
            completed.add(ym)
            continue

        print(f"— {len(all_files)} files", end=" ", flush=True)

        if DRY_RUN:
            # 只显示统计
            by_ex = {}
            for f in all_files:
                ex = f.split("/")[0]
                by_ex[ex] = by_ex.get(ex, 0) + 1
            print(f"({', '.join(f'{k}:{v}' for k,v in sorted(by_ex.items()))})")
            completed.add(ym)
            continue

        # 2. 写 listfile
        listfile = os.path.join(OUT_BASE, "_temp_list.txt")
        with open(listfile, "w", encoding="utf-8") as lf:
            for f in all_files:
                lf.write(f + "\n")

        # 3. 提取到临时目录
        temp_dir = os.path.join(OUT_BASE, "_temp_extract")
        success, output = extract_files(rar_path, listfile, temp_dir)

        if not success:
            print(f"— EXTRACT FAILED")
            continue

        # 4. 重组文件
        organized = 0
        for f in all_files:
            info = parse_contract_info(f)
            src_path = os.path.join(temp_dir, f.replace("\\", os.sep).replace("/", os.sep))
            if organize_file(src_path, OUT_BASE, info):
                organized += 1

        # 5. 清理临时文件
        if os.path.exists(temp_dir):
            shutil.rmtree(temp_dir, ignore_errors=True)
        if os.path.exists(listfile):
            os.remove(listfile)

        elapsed = time.time() - t0
        total_extracted += len(all_files)
        total_organized += organized
        print(f"— {organized} new files ({elapsed:.0f}s)")

        # 记录进度
        completed.add(ym)
        with open(progress_file, "w") as pf:
            for c in sorted(completed):
                pf.write(c + "\n")

    # ─── 汇总 ───
    total_time = time.time() - t_start
    print()
    print("=" * 60)
    print(f"Done: {len(completed)}/{len(rars)} RARs in {total_time/60:.0f} min")
    print(f"Total files extracted: {total_extracted:,}")
    if not DRY_RUN:
        print(f"Total files organized: {total_organized:,}")

    # 磁盘使用
    if not DRY_RUN:
        total_size = 0
        total_files = 0
        for root, dirs, files in os.walk(OUT_BASE):
            for f in files:
                if f.endswith('.csv'):
                    total_size += os.path.getsize(os.path.join(root, f))
                    total_files += 1
        print(f"Tick CSV on disk: {total_files:,} files, {total_size/1024/1024/1024:.2f} GB")
    print("=" * 60)

if __name__ == "__main__":
    main()
