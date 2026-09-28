#!/usr/bin/env python3
"""
研究结论到期复核 — 回答「文档里写的失效条件，触发了没有？」

用法:
  python scripts/daily/review_due.py              # 扫描全部研究文档
  python scripts/daily/review_due.py 30           # 只看文档日期在近 30 天内的

原理:
  研究文档里的失效判据是散文（9 种句式、措辞不统一），解析语义不可靠。
  能可靠的只有两样: 价位数字，和真实 K 线。所以本脚本只做「价位核对」:
    1. 抽年龄（frontmatter date → 文件名日期，取不到就标 UNKNOWN，不猜）
    2. 抽价位（正则抓「跌破/止损/证伪…」旁的数字）
    3. 拉真实日线核对（akshare，期货走 Sina / 股票走东财）
  判断留给人，脚本只报状态。

数据来源:
  历史库停在 2026-06-30，live 库只有近几日，中间约 2.5 个月是空的，
  而研究文档几乎全落在缺口里 —— 故不复用 DuckDB，改走 akshare 取近期日线。

输出:
  stdout  — 干净报告（可直接粘进日志）
  stderr  — 进度与警告
  output/review_due_YYYY-MM-DD.md — 同内容落盘（gitignored）

只读: 绝不修改 docs/ 下任何文档。
"""

import os
import re
import sys
import json
import datetime

import akshare as ak

# Windows GBK 终端下 emoji 与中文输出兼容（房风格：chanlun 系脚本通用写法）
sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

import warnings

warnings.filterwarnings("ignore")

# ── 路径 ──────────────────────────────────────────
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(os.path.dirname(SCRIPT_DIR))
DOCS_ROOT = os.path.join(REPO_ROOT, "docs", "trading")
OUT_DIR = os.path.join(SCRIPT_DIR, "output")
CACHE_DIR = os.path.join(OUT_DIR, "cache")

SCAN_DIRS = ["品种研究", "个股研究"]

# 判定阈值
NEAR_PCT = 3.0    # 距判据 <3% 视为「临近」
FAR_PCT = 80.0    # 判据距现价 >80% 视为超出核对范围（历史叙述/远期目标/噪声）
STALE_PCT = 15.0  # 写作时判据已偏离价位 >15% 视为「不在场」，非前瞻性触发

# 否定前缀: 「未突破前高7604」不是判据，「若突破5382」才是
NEG = r"(?<!未)(?<!没)(?<!无法)(?<!不能)(?<!尚未)(?<!难以)"

# ── 正则在下面定义 ────────────────────────────────
# 向下判据关键词（失效/止损类）
DOWN_KW = r"跌破|跌穿|失守|止损|证伪|结构底线|分水岭|离场"
# 向上判据关键词（确认/突破类）
UP_KW = r"突破|站上|站稳"

# 价位: 1~7 位整数，可带小数（个位数股价如 9.10/8.00 很常见，不能要求 2 位以上）。
# 排除日期数字（后接 - / / / 年月日）
NUM = r"(\d{1,7}(?:\.\d+)?)"
DATE_TAIL = r"(?![\d\-/年月日])"
# 关键词与价位之间不允许出现句读 —— 防止跨句误配（如「证伪…介于 520500 与 002812」）
GAP = r"[^0-9\n。，；！？、（）()「」《》　]{0,8}"

RE_DOWN_AFTER = re.compile(rf"{NEG}(?:{DOWN_KW}){GAP}{NUM}{DATE_TAIL}")
RE_DOWN_BEFORE = re.compile(rf"{NUM}\s*{NEG}(?:{DOWN_KW})")
RE_UP_AFTER = re.compile(rf"{NEG}(?:{UP_KW}){GAP}{NUM}{DATE_TAIL}")

# 噪声特征：判据数字后面紧跟这些，说明它不是价格
RE_NOISE_TAIL = re.compile(r"^\s*(?:%|％|均线|日均线|周均线|月均线|日线|周线|月线|倍|手|股|元/吨|万吨)")
# 噪声特征：判据数字前面紧跟这些
RE_NOISE_HEAD = re.compile(r"(?:MA|ma|均线|第)\s*$")

# frontmatter: 兼容 5 种方言（date / created / 无）
RE_FM_DATE = re.compile(r"^(?:date|created)\s*:\s*[\"']?(\d{4})-(\d{2})-(\d{2})", re.M)
# 文档自己声明的复核日期（见 docs/trading/模板/研究分析模板.md）
RE_FM_REVIEW = re.compile(r"^review_by\s*:\s*[\"']?(\d{4})-(\d{2})-(\d{2})", re.M)
# 文件名日期: -YYYY-MM-DD / _YYYYMMDD / _YYYY-MM-DD
RE_FN_DATE_DASH = re.compile(r"(\d{4})-(\d{2})-(\d{2})")
RE_FN_DATE_COMPACT = re.compile(r"_(\d{4})(\d{2})(\d{2})(?!\d)")

# 标的解析
RE_STOCK = re.compile(r"(?<!\d)([036]\d{5})(?!\d)")  # 6 位股票代码
RE_FUT_FULL = re.compile(r"(?<![A-Za-z0-9])([A-Za-z]{1,2}\d{3,4})(?![A-Za-z0-9])")  # AG2612 / SA2701
RE_FUT_CONT = re.compile(r"(?<![A-Za-z0-9])([A-Za-z]{1,2}0)(?![A-Za-z0-9])")  # RU0 / AG0

# 品种白名单 —— 从 symbols.json 载入（75 品种），用于剔除误配（如 B127 这种假合约）
_PRODUCT_CACHE = []


def product_codes():
    if not _PRODUCT_CACHE:
        path = os.path.join(REPO_ROOT, "src", "TradingStudio", "symbols.json")
        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
            _PRODUCT_CACHE.append({s["code"].upper() for s in data["symbols"]})
        except Exception as e:
            log(f"  [警告] 无法载入 symbols.json（品种白名单失效）: {e}")
            _PRODUCT_CACHE.append(set())
    return _PRODUCT_CACHE[0]


def log(msg):
    """进度与警告一律走 stderr，保持 stdout 干净。"""
    print(msg, file=sys.stderr)


# ═══════════════════════════════════════════════
# 一、扫描文档
# ═══════════════════════════════════════════════

def read_doc(path):
    """读文档，返回 (text, ok)。绝不抛异常。"""
    for enc in ("utf-8", "gbk", "utf-8-sig"):
        try:
            with open(path, "r", encoding=enc) as f:
                return f.read()
        except UnicodeDecodeError:
            continue
        except Exception as e:
            log(f"  [警告] 读取失败 {os.path.basename(path)}: {e}")
            return None
    log(f"  [警告] 编码无法识别: {os.path.basename(path)}")
    return None


def extract_doc_date(text, filename):
    """抽文档日期。优先级: frontmatter → 文件名。取不到返回 None（不猜）。"""
    m = RE_FM_DATE.search(text[:2000])
    if m:
        try:
            return datetime.date(int(m.group(1)), int(m.group(2)), int(m.group(3)))
        except ValueError:
            pass
    m = RE_FN_DATE_DASH.search(filename) or RE_FN_DATE_COMPACT.search(filename)
    if m:
        try:
            return datetime.date(int(m.group(1)), int(m.group(2)), int(m.group(3)))
        except ValueError:
            pass
    return None


def extract_review_by(text):
    """抽文档自己声明的复核日期（review_by）。未声明返回 None。"""
    m = RE_FM_REVIEW.search(text[:2000])
    if m:
        try:
            return datetime.date(int(m.group(1)), int(m.group(2)), int(m.group(3)))
        except ValueError:
            pass
    return None


def extract_levels(text):
    """抽价位判据，返回 [(kind, value), ...]，kind ∈ {'down','up'}。"""
    found = {}
    for m in RE_DOWN_AFTER.finditer(text):
        v = _num(m.group(1), text, m.end(1))
        if v is not None:
            found.setdefault(("down", v), m.start())
    for m in RE_DOWN_BEFORE.finditer(text):
        v = _num(m.group(1), text, m.end(1))
        if v is not None:
            found.setdefault(("down", v), m.start())
    for m in RE_UP_AFTER.finditer(text):
        v = _num(m.group(1), text, m.end(1))
        if v is not None:
            found.setdefault(("up", v), m.start())
    # 按出现顺序返回
    return [k for k, _ in sorted(found.items(), key=lambda kv: kv[1])]


def _num(s, text, end_pos):
    """字符串转价位，并过滤已知噪声。text/end_pos 用于看数字后文的上下文。"""
    try:
        v = float(s)
    except ValueError:
        return None
    # 年份区间内的整数 → 大概率是日期碎片
    if v == int(v) and 1990 <= v <= 2100:
        return None
    # 过小的数（如月份、手数）不像价格判据
    if v < 1:
        return None
    # 6 位及以上整数 → 股票/ETF 代码（000690 / 520500），不是价位
    if v >= 100000:
        return None

    head = text[max(0, end_pos - len(s) - 6): end_pos - len(s)]
    tail = text[end_pos: end_pos + 8]
    # 后接 % / 均线 / 手 / 倍 … → 不是价位
    if RE_NOISE_TAIL.match(tail):
        return None
    # 前接 MA / 均线 / 第 … → 不是价位
    if RE_NOISE_HEAD.search(head):
        return None
    return v


# 指数 → 新浪代码（研究文档里有「上证指数周线缠论分析」这类）
INDEX_MAP = {
    "上证指数": "sh000001", "深证成指": "sz399001", "创业板指": "sz399006",
    "沪深300": "sh000300", "中证500": "sh000905", "上证50": "sh000016",
    "科创50": "sh000688", "中证1000": "sh000852",
}


def _valid_futures(sym, products):
    """校验期货代码: 品种在白名单内 且 月份合法。
    单字母品种（b/c/j/m/p/v/i/l/y）极易误配，故必须卡月份：
    B149 → YMM 里 MM=49 非法 → 拒绝；SA701 → 2027-01 ✓；AG2612 → 2026-12 ✓
    """
    m = re.match(r"^([A-Z]+)(\d+)$", sym.upper())
    if not m:
        return False
    prod, digits = m.group(1), m.group(2)
    if prod not in products:
        return False
    if digits == "0":
        return True                      # 连续合约 XX0
    if len(digits) in (3, 4):
        mm = int(digits[-2:])
        return 1 <= mm <= 12             # 交割月必须 01~12
    return False


def extract_instruments(text, filename):
    """抽标的，返回 [(kind, symbol), ...]，kind ∈ {'stock','fut'}。

    优先级（文件名最可靠，研究文档的标的就在文件名里）:
      1. 文件名里的完整合约 AG2612 / 连续合约 AG0 / 股票代码 601600
      2. 文件名里的裸品种码 JM / RU / SA → 用连续合约 JM0 / RU0 / SA0
      3. 正文兜底
    所有期货符号都要过品种白名单，挡掉 B127 这类假合约。
    """
    products = product_codes()
    stem = os.path.splitext(filename)[0]
    out, seen = [], set()

    def add(kind, sym):
        sym = sym.upper()
        if kind == "fut" and not _valid_futures(sym, products):
            return
        key = (kind, sym)
        if key in seen:
            return
        seen.add(key)
        out.append((kind, sym))

    # 0) 文件名: 指数（名称最独特，优先）
    for cn, sym in INDEX_MAP.items():
        if cn in stem:
            return [("index", sym)]

    # 1) 文件名: 完整 / 连续合约、股票代码
    for m in RE_FUT_FULL.finditer(stem):
        add("fut", m.group(1))
    for m in RE_FUT_CONT.finditer(stem):
        add("fut", m.group(1))
    for m in RE_STOCK.finditer(stem):
        add("stock", m.group(1))
    if out:
        return out[:3]

    # 2) 文件名: 裸品种码（先试 2 字符，再退 1 字符）→ 连续合约
    for pat in (r"([A-Za-z]{2})(?=[一-鿿]|[-_.·]|$)",
                r"([A-Za-z])(?=[一-鿿]|[-_.·]|$)"):
        for m in re.finditer(pat, stem):
            add("fut", m.group(1) + "0")
        if out:
            return out[:3]

    # 3) 正文兜底
    for m in RE_FUT_FULL.finditer(text):
        add("fut", m.group(1))
    for m in RE_FUT_CONT.finditer(text):
        add("fut", m.group(1))
    for m in RE_STOCK.finditer(text):
        add("stock", m.group(1))

    return out[:3]


def normalize_futures_symbol(sym):
    """CZCE 文档常写 4 位 YYMM（SA2701），实际交易所记法是 3 位 YMM（SA701）。
    但 Sina 接口用的是文档那套 4 位记法，故此处保留原样，仅供展示/兜底。"""
    return sym


# ═══════════════════════════════════════════════
# 二、取日线（带当日缓存）
# ═══════════════════════════════════════════════

def _fetch_stock(symbol, start_date):
    """股票日线。东财接口常断连，故 东财(重试) → 新浪(兜底) 双通道。
    返回 [(date, close, low, high), ...]，全失败返回 None。"""
    import time

    # 通道一：东财
    for attempt in range(2):
        try:
            df = ak.stock_zh_a_hist(
                symbol=symbol, period="daily",
                start_date=start_date.strftime("%Y%m%d"),
                end_date=datetime.date.today().strftime("%Y%m%d"),
            )
            if df is not None and not df.empty:
                return [(r["日期"], float(r["收盘"]), float(r["最低"]), float(r["最高"]))
                        for _, r in df.iterrows()]
        except Exception:
            if attempt == 0:
                time.sleep(1.5)
                continue

    # 通道二：新浪（仓库既有写法，见 scripts/chanlun/chanlun_batch.py）
    try:
        prefix = "sh" if symbol.startswith("6") else "sz"
        df = ak.stock_zh_a_daily(
            symbol=f"{prefix}{symbol}",
            start_date=start_date.strftime("%Y%m%d"),
            end_date=datetime.date.today().strftime("%Y%m%d"),
            adjust="",
        )
        if df is not None and not df.empty:
            return [(r["date"], float(r["close"]), float(r["low"]), float(r["high"]))
                    for _, r in df.iterrows()]
    except Exception as e:
        log(f"  [警告] 股票双通道均失败 {symbol} — {type(e).__name__}: {str(e)[:50]}")

    return None


def fetch_daily(kind, symbol, start_date):
    """取日线，返回 [(date, close, low, high), ...]。失败返回 None。"""
    cache_key = f"{kind}_{symbol}.json"
    cache_path = os.path.join(CACHE_DIR, cache_key)
    today = datetime.date.today().isoformat()

    # 缓存：同一天内复用（幂等 + 避免重复打接口）
    if os.path.exists(cache_path):
        try:
            with open(cache_path, "r", encoding="utf-8") as f:
                cached = json.load(f)
            if cached.get("fetched") == today:
                return [(datetime.date.fromisoformat(d), c, lo, hi) for d, c, lo, hi in cached["rows"]]
        except Exception:
            pass  # 缓存坏了就重取，不中断

    try:
        if kind == "index":
            df = ak.stock_zh_index_daily(symbol=symbol)
            if df is None or df.empty:
                return None
            rows = [
                (r["date"], float(r["close"]), float(r["low"]), float(r["high"]))
                for _, r in df.iterrows()
            ]
        elif kind == "stock":
            rows = _fetch_stock(symbol, start_date)
        else:
            # Sina 返回该合约全部历史；不按 start_date 预筛，
            # 好让缓存能被同一标的的其它文档（不同日期）复用
            df = ak.futures_zh_daily_sina(symbol=normalize_futures_symbol(symbol))
            if df is None or df.empty:
                return None
            rows = [
                (r["date"], float(r["close"]), float(r["low"]), float(r["high"]))
                for _, r in df.iterrows()
            ]
    except Exception as e:
        log(f"  [警告] 取数失败 {kind}:{symbol} — {type(e).__name__}: {str(e)[:60]}")
        return None

    if not rows:
        return None

    try:
        os.makedirs(CACHE_DIR, exist_ok=True)
        with open(cache_path, "w", encoding="utf-8") as f:
            json.dump(
                {"fetched": today, "rows": [[d.isoformat() if hasattr(d, "isoformat") else str(d), c, lo, hi] for d, c, lo, hi in rows]},
                f, ensure_ascii=False,
            )
    except Exception as e:
        log(f"  [警告] 缓存写入失败 {cache_key}: {e}")

    return [(d if isinstance(d, datetime.date) else datetime.date.fromisoformat(str(d)), c, lo, hi) for d, c, lo, hi in rows]


# ═══════════════════════════════════════════════
# 三、核对
# ═══════════════════════════════════════════════

def check_level(kind, level, bars, since_date):
    """把判据价位拿去核对真实 K 线。
    返回 (status, detail)，status ∈ {'触发','临近','安全','无法核对'}。

    语义：只关心「文档日期之后」的穿越 —— 文档写作前就已穿越的，
    说明作者本来就知道，不算新闻（这也是最常见的误报源）。
    """
    if not bars:
        return "无法核对", "无 K 线数据"

    # 只保留文档日期之后的 K 线
    after = [b for b in bars if since_date is None or b[0] > since_date]
    if not after:
        return "无法核对", "文档日期后无 K 线"

    last_close = after[-1][1]
    at_doc = after[0][1]  # 文档写作前后的价位，用于判断判据当时是否「在场」

    # 写作时就已被远远满足的判据 → 不是前瞻性触发（如现价 33 却写「站上 15」）
    if kind == "up" and level < at_doc * (1 - STALE_PCT / 100):
        return "无法核对", f"文档写作时价位 {at_doc:g} 已远在判据 {level:g} 上方"
    if kind == "down" and level > at_doc * (1 + STALE_PCT / 100):
        return "无法核对", f"文档写作时价位 {at_doc:g} 已远在判据 {level:g} 下方"

    # 距现价过远 → 历史叙述/远期目标/量级不符，超出核对范围。诚实标注，不硬判「触发」。
    far = abs(level - last_close) / last_close * 100
    if far > 500:
        return "无法核对", f"判据 {level:g} 与现价 {last_close:g} 量级不符"
    if far > FAR_PCT:
        return "无法核对", f"判据 {level:g} 距现价 {last_close:g} 达 {far:.0f}%，超出核对范围"

    if kind == "down":
        breached = [(b[0], b[2]) for b in after if b[2] < level]
        if breached:
            d0 = breached[0][0]
            return "触发", f"已于 {d0} 跌破（最低 {breached[0][1]:g}）"
        dist = (last_close - level) / level * 100 if level else 0
        tag = "临近" if dist < NEAR_PCT else "安全"
        return tag, f"现价 {last_close:g}，距判据 +{dist:.1f}%"
    else:
        breached = [(b[0], b[3]) for b in after if b[3] > level]
        if breached:
            d0 = breached[0][0]
            return "触发", f"已于 {d0} 突破（最高 {breached[0][1]:g}）"
        dist = (level - last_close) / last_close * 100 if level else 0
        tag = "临近" if dist < NEAR_PCT else "安全"
        return tag, f"现价 {last_close:g}，距判据 -{dist:.1f}%"


# ═══════════════════════════════════════════════
# 主流程
# ═══════════════════════════════════════════════

def collect_docs(max_age_days=None):
    """遍历研究文档，返回 [(relpath, doc_date, levels, instruments), ...]"""
    docs = []
    today = datetime.date.today()

    for sub in SCAN_DIRS:
        d = os.path.join(DOCS_ROOT, sub)
        if not os.path.isdir(d):
            log(f"  [警告] 目录不存在，跳过: {sub}")
            continue
        for name in sorted(os.listdir(d)):
            if not name.endswith(".md"):
                continue
            path = os.path.join(d, name)
            text = read_doc(path)
            if text is None:
                continue

            doc_date = extract_doc_date(text, name)
            if max_age_days is not None:
                if doc_date is None:
                    continue
                if (today - doc_date).days > max_age_days:
                    continue

            levels = extract_levels(text)
            instruments = extract_instruments(text, name)
            review_by = extract_review_by(text)
            docs.append((os.path.join(sub, name), doc_date, levels, instruments, review_by))

    return docs


def main():
    today = datetime.date.today()

    # 位置参数: 可选「只看近 N 天」（房风格，无 argparse）
    max_age_days = None
    if len(sys.argv) > 1:
        try:
            max_age_days = int(sys.argv[1])
        except ValueError:
            print(f"用法: python {os.path.basename(__file__)} [只看近N天]", file=sys.stderr)
            sys.exit(1)

    log(f"扫描 {DOCS_ROOT} ...")
    docs = collect_docs(max_age_days)
    log(f"  找到 {len(docs)} 篇研究文档")

    results = []
    for relpath, doc_date, levels, instruments, review_by in docs:
        name = os.path.basename(relpath)
        base = {"doc": relpath, "date": doc_date, "review_by": review_by, "items": []}

        if not levels:
            results.append({**base, "status": "无法核对", "reason": "未抽到价位判据"})
            continue
        if not instruments:
            results.append({**base, "status": "无法核对", "reason": "未解析出标的"})
            continue
        if doc_date is None:
            results.append({**base, "status": "无法核对", "reason": "无日期（不猜）"})
            continue

        # 取 K 线: 从文档日期起（只关心文档写作之后的穿越）
        kind, symbol = instruments[0]
        bars = fetch_daily(kind, symbol, doc_date)
        log(f"  {name[:40]:42s} {symbol:10s} bars={len(bars) if bars else 0}")

        items = []
        for lk, lv in levels:
            status, detail = check_level(lk, lv, bars, doc_date)
            items.append({"kind": lk, "level": lv, "status": status, "detail": detail})

        if not items:
            overall = "无法核对"
        elif any(i["status"] == "触发" for i in items):
            overall = "触发"
        elif any(i["status"] == "临近" for i in items):
            overall = "临近"
        elif all(i["status"] == "无法核对" for i in items):
            overall = "无法核对"
        else:
            overall = "安全"

        results.append({**base, "symbol": symbol, "status": overall, "items": items})

    # ── 出报告 ────────────────────────────────────
    order = {"触发": 0, "临近": 1, "安全": 2, "无法核对": 3}
    results.sort(key=lambda r: (order.get(r["status"], 9), r["doc"]))

    L = []
    L.append("═" * 62)
    L.append(f"  研究结论到期复核 — {today}")
    L.append("═" * 62)
    L.append("")

    counts = {}
    for r in results:
        counts[r["status"]] = counts.get(r["status"], 0) + 1
    L.append("  " + " | ".join(f"{k} {v}" for k, v in sorted(counts.items(), key=lambda kv: order.get(kv[0], 9))))

    overdue = [r for r in results if r.get("review_by") and r["review_by"] < today]
    if overdue:
        L.append("")
        L.append(f"  ⏰ 已过自声明复核日期 ({len(overdue)})：")
        for r in sorted(overdue, key=lambda r: r["review_by"]):
            L.append(f"     {r['doc']}  应于 {r['review_by']} 复核（逾期 {(today - r['review_by']).days} 天）")
    L.append("")

    icons = {"触发": "⚠️ ", "临近": "🔸", "安全": "✅", "无法核对": "❔"}
    for status in ("触发", "临近", "安全", "无法核对"):
        rs = [r for r in results if r["status"] == status]
        if not rs:
            continue
        L.append(f"── {icons[status]} {status} ({len(rs)}) " + "─" * 30)
        for r in rs:
            age = f"{(today - r['date']).days}天前" if r["date"] else "无日期"
            L.append(f"  {r['doc']}")
            L.append(f"    {r['date'] or '—'} ({age})" + (f"  {r.get('symbol','')}" if r.get("symbol") else ""))
            if r.get("reason"):
                L.append(f"    {r['reason']}")
            for i in r["items"]:
                arrow = "跌破" if i["kind"] == "down" else "站上"
                L.append(f"    {icons.get(i['status'],'')} {arrow} {i['level']:g} → {i['status']} · {i['detail']}")
        L.append("")

    L.append("─" * 62)
    L.append("  说明：本表只做价位核对，不做判断。判据语义请回看原文档。")
    L.append("  数据：akshare 近期日线（历史库止于 2026-06-30，7-9 月为缺口）")

    report = "\n".join(L)

    print(report)

    try:
        os.makedirs(OUT_DIR, exist_ok=True)
        out_path = os.path.join(OUT_DIR, f"review_due_{today}.md")
        with open(out_path, "w", encoding="utf-8") as f:
            f.write(report + "\n")
        log(f"\n报告已落盘: {out_path}")
    except Exception as e:
        log(f"[警告] 报告落盘失败: {e}")


if __name__ == "__main__":
    main()
