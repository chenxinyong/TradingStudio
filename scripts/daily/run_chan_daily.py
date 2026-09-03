#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
每天自动跑缠论：三大主指数（周线）+ 持仓合约（周线+日线），追加到每日日志。
用法: python run_chan_daily.py [YYYY-MM-DD]
依赖: 已编译的 ChanAnalysis（dotnet build 一次即可，运行时用 --no-build）

覆盖范围（与用户确认：指数 + 持仓合约）：
  - 指数：国证A股 / 创业板 / 科创50（仅周线，min_gap=4）
  - 合约：持仓跟踪里的品种（周线+日线，min_gap=2）

输出：分别追加到 docs/trading/Notes/股票/YYYY-MM/<date>.md 与 docs/trading/Notes/期货/YYYY-MM/<date>.md，
      插入位置在「链接」段之前，若已含缠论段则跳过（幂等）。
"""
import subprocess, os, sys, re
from datetime import date

VAULT = r"C:\Users\chenx\OneDrive\MyFiles\DialyNotes\Trading"
NOTES = r"c:\Works\ClaudeCode\TradingStudio\docs\trading\Notes"
CSHARP = r"c:\Works\ClaudeCode\TradingStudio\tools\ChanAnalysis"

RUN_DATE = sys.argv[1] if len(sys.argv) > 1 else date.today().isoformat()

# ===== 配置 =====
# 三大主指数（口径见记忆 primary-a-share-indices：国证A股/创业板/科创50）
INDICES = [
    ("sz399317", "国证A股"),
    ("sz399006", "创业板"),
    ("sh000688", "科创50"),
]

# 持仓合约（周线+日线）—— 按最新日志「持仓跟踪」维护，换月/平仓后更新此处
# 口径说明：此处用「新浪 4 位代码」（YYMM），与 CTP「交易所代码」的对应关系：
#   郑商所 CTP 用 3 位（YMM），新浪扩展为 4 位：SA2701(新浪) = sa701(CTP 纯碱2027年1月)
#   上期所本身即 4 位（YYMM），无差异：AG2612(新浪) = ag2612(CTP 白银2026年12月)
FUTURES = [
    ("AG2612", "白银"),
    ("SA2701", "纯碱"),
]


def run_csharp(mode, code, name):
    """调用 ChanAnalysis，返回 markdown 字符串；失败返回 None。"""
    cmd = ["dotnet", "run", "--no-build", "--", mode, code, name]
    try:
        r = subprocess.run(cmd, cwd=CSHARP, capture_output=True, timeout=60)
    except (subprocess.TimeoutExpired, OSError) as e:
        print(f"  [警告] {code} {name} 执行失败: {e}")
        return None
    if r.returncode != 0:
        err = r.stderr.decode("utf-8", errors="replace").strip()
        print(f"  [警告] {code} {name} 返回码 {r.returncode}: {err[:200]}")
        return None
    # C# 已设 Console.OutputEncoding=UTF8，先按 UTF-8 解码，失败退回 GBK
    for enc in ("utf-8", "gbk"):
        try:
            return r.stdout.decode(enc)
        except UnicodeDecodeError:
            continue
    return r.stdout.decode("utf-8", errors="replace")


def insert_section(log_path, header, banner, blocks):
    """把缠论段插入日志「链接」段之前；已存在则跳过；返回 True 表示已写入。"""
    if not os.path.exists(log_path):
        print(f"  日志 {log_path} 不存在，请先运行 log_generator.py")
        return False

    with open(log_path, "r", encoding="utf-8") as f:
        content = f.read()

    if header in content:
        print(f"  已含「{header}」段，跳过。")
        return False

    # 定位「链接」段（标题形如 "## 七、链接" / "## 十、链接"）
    m = re.search(r"^## .*链接.*$", content, flags=re.M)
    if not m:
        # 兜底：最后一个 --- 分隔线
        m = re.search(r"\n---\n", content)
        insert_pos = m.start() if m else -1
    else:
        insert_pos = m.start()

    if insert_pos == -1:
        print(f"  无法定位插入位置：{log_path}")
        return False

    lines = ["", "---", "", header, "", banner, ""]
    lines += [b.rstrip() for b in blocks if b]
    new_content = content[:insert_pos] + "\n".join(lines) + "\n" + content[insert_pos:]

    with open(log_path, "w", encoding="utf-8") as f:
        f.write(new_content)
    return True


def main():
    # 1. 指数 → 股票日志
    stock_log = os.path.join(NOTES, "股票", RUN_DATE[:7], f"{RUN_DATE}.md")
    idx_blocks = []
    print(f"[指数] 缠论分析（周线）")
    for code, name in INDICES:
        md = run_csharp("mdindex", code, name)
        if md:
            idx_blocks.append(md)
            print(f"  [OK] {name} {code}")
    if idx_blocks and os.path.exists(stock_log):
        done = insert_section(
            stock_log,
            "## 缠论分析（指数周线）",
            f"> 自动生成：scripts/run_chan_daily.py | {RUN_DATE} | 数据：新浪指数日K聚合周线 | min_gap=4",
            idx_blocks,
        )
        if done:
            print(f"  → 已追加到 股票/{RUN_DATE}.md")

    # 2. 持仓合约 → 期货日志
    fut_log = os.path.join(NOTES, "期货", RUN_DATE[:7], f"{RUN_DATE}.md")
    fut_blocks = []
    print(f"[合约] 缠论分析（周线+日线）")
    for code, name in FUTURES:
        md = run_csharp("md", code, name)
        if md:
            fut_blocks.append(md)
            print(f"  [OK] {name} {code}")
    if fut_blocks and os.path.exists(fut_log):
        done = insert_section(
            fut_log,
            "## 缠论分析（持仓合约）",
            f"> 自动生成：scripts/run_chan_daily.py | {RUN_DATE} | 数据：新浪期货日K | min_gap=2",
            fut_blocks,
        )
        if done:
            print(f"  → 已追加到 期货/{RUN_DATE}.md")

    print("完成。")


if __name__ == "__main__":
    main()
