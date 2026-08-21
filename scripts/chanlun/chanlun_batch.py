"""
缠论批量分析 — 四类标的统一管线
==================================
个股 / 板块(申万行业) / 指数 / 期货品种

数据源:
  期货   -> akshare futures_zh_daily_sina (新浪, 连续合约, 代码加 '0')
  指数   -> akshare stock_zh_index_daily   (新浪)
  个股   -> akshare stock_zh_a_daily qfq   (新浪, 前复权)
  板块   -> akshare index_hist_sw           (申万行业指数)

管线:
  日线 -> 重采样周线(W-FRI) -> 缠论五步(包含/分型/笔/中枢) -> MACD背驰 -> 结构化JSON

用法:
  python scripts/chanlun/chanlun_batch.py [--cat futures|stocks|indices|sectors] [--limit N]
"""

from __future__ import annotations

import io
import json
import os
import sys
import time
import warnings
from dataclasses import dataclass, field
from datetime import datetime
from typing import List, Dict, Any

warnings.filterwarnings("ignore")
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

import numpy as np
import pandas as pd
import akshare as ak

# chanlun_core 与脚本同目录
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from chanlun_core import Bar, Direction, analyze as chanlun_analyze  # noqa: E402

# ═══════════════════════════════════════════════
# 参数
# ═══════════════════════════════════════════════
MIN_BI_LEN = 5          # 周线最小笔长度
ZS_MIN_OVERLAP = 0.005  # 中枢最小重叠比例
DIVERGENCE_RATIO = 0.7  # 背驰力度比阈值
MACD_FAST, MACD_SLOW, MACD_SIGNAL = 12, 26, 9
OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "output", "chanlun_batch")

# ═══════════════════════════════════════════════
# 标的清单 (聚焦核心集)
# ═══════════════════════════════════════════════

FUTURES = {
    # 贵金属
    "AU0": "黄金", "AG0": "白银",
    # 有色金属
    "CU0": "铜", "AL0": "铝", "ZN0": "锌", "NI0": "镍", "SN0": "锡",
    # 黑色系
    "RB0": "螺纹钢", "HC0": "热卷", "I0": "铁矿", "JM0": "焦煤", "J0": "焦炭",
    # 能源
    "SC0": "原油", "FU0": "燃料油",
    # 化工
    "TA0": "PTA", "MA0": "甲醇", "V0": "PVC", "EG0": "乙二醇", "SA0": "纯碱", "FG0": "玻璃",
    # 农产品
    "M0": "豆粕", "Y0": "豆油", "P0": "棕榈油", "SR0": "白糖", "CF0": "棉花", "RM0": "菜粕",
    # 新能源
    "LC0": "碳酸锂", "SI0": "工业硅", "RU0": "天然橡胶",
}

INDICES = {
    "sh000001": "上证指数", "sz399001": "深证成指", "sz399006": "创业板指",
    "sh000688": "科创50", "sh000016": "上证50", "sh000300": "沪深300",
    "sh000905": "中证500", "sz399852": "中证1000",
}

STOCKS = {
    # L01 AI算力基座
    "300308": "中际旭创", "601138": "工业富联", "300476": "胜宏科技",
    "002463": "沪电股份", "002837": "英维克",
    # L02 半导体
    "002371": "北方华创", "688012": "中微公司", "688072": "拓荆科技",
    "688981": "中芯国际", "600584": "长电科技",
    # L04 AI电力设备
    "000338": "潍柴动力", "688676": "金盘科技", "600885": "宏发股份",
    # L06 储能
    "300750": "宁德时代", "600406": "国电南瑞",
    # L07 高端装备
    "300124": "汇川技术", "002008": "大族激光", "688305": "科德数控",
    # L09 电子元器件
    "000636": "风华高科", "002475": "立讯精密",
    # 持仓
    "688271": "联影医疗",
}

SECTORS = {
    # 九层AI产业链 -> 申万行业指数
    "801102": "通信设备(L01算力)",
    "801081": "半导体(L02)",
    "801104": "软件开发(L03应用)",
    "801730": "电力设备(L04)",
    "801735": "光伏设备(L05)",
    "801737": "电池(L06储能)",
    "801078": "自动化设备(L07机器人)",
    "801085": "消费电子(L08)",
    "801055": "工业金属(L09)",
}


def sina_stock_code(code: str) -> str:
    """6xxxxx/68xxxx -> sh, 0xxxxx/3xxxxx -> sz, 4xxxxx/8xxxxx -> bj"""
    if code.startswith(("6", "9")):
        return f"sh{code}"
    if code.startswith(("4", "8")):
        return f"bj{code}"
    return f"sz{code}"


def _retry(fn, tries=4, wait=1.5):
    last = None
    for i in range(tries):
        try:
            return fn()
        except Exception as e:  # noqa: BLE001
            last = e
            time.sleep(wait * (i + 1))
    raise last


# ═══════════════════════════════════════════════
# 数据加载 (日线 DataFrame -> 列 Date/Open/High/Low/Close/Volume)
# ═══════════════════════════════════════════════

def load_futures(symbol: str) -> pd.DataFrame:
    df = _retry(lambda: ak.futures_zh_daily_sina(symbol=symbol))
    df = df.rename(columns={"date": "Date", "open": "Open", "high": "High",
                            "low": "Low", "close": "Close", "volume": "Volume"})
    df["Date"] = pd.to_datetime(df["Date"])
    return df[["Date", "Open", "High", "Low", "Close", "Volume"]].dropna()


def load_index(symbol: str) -> pd.DataFrame:
    df = _retry(lambda: ak.stock_zh_index_daily(symbol=symbol))
    df = df.rename(columns={"date": "Date", "open": "Open", "high": "High",
                            "low": "Low", "close": "Close", "volume": "Volume"})
    df["Date"] = pd.to_datetime(df["Date"])
    return df[["Date", "Open", "High", "Low", "Close", "Volume"]].dropna()


def load_stock(code: str) -> pd.DataFrame:
    sym = sina_stock_code(code)
    df = _retry(lambda: ak.stock_zh_a_daily(symbol=sym, adjust="qfq"))
    df = df.rename(columns={"date": "Date", "open": "Open", "high": "High",
                            "low": "Low", "close": "Close", "volume": "Volume"})
    df["Date"] = pd.to_datetime(df["Date"])
    return df[["Date", "Open", "High", "Low", "Close", "Volume"]].dropna()


def load_sector(code: str) -> pd.DataFrame:
    df = _retry(lambda: ak.index_hist_sw(symbol=code, period="day"))
    df = df.rename(columns={"日期": "Date", "开盘": "Open", "最高": "High",
                            "最低": "Low", "收盘": "Close", "成交量": "Volume"})
    df["Date"] = pd.to_datetime(df["Date"])
    return df[["Date", "Open", "High", "Low", "Close", "Volume"]].dropna()


LOADERS = {
    "futures": load_futures,
    "indices": load_index,
    "stocks": load_stock,
    "sectors": load_sector,
}


def resample_weekly(df: pd.DataFrame) -> pd.DataFrame:
    """日线 -> 周线 (W-FRI)"""
    df = df.set_index("Date")
    w = df.resample("W-FRI").agg({
        "Open": "first", "High": "max", "Low": "min", "Close": "last", "Volume": "sum",
    }).dropna().reset_index()
    return w


# ═══════════════════════════════════════════════
# 背驰 (MACD 面积比)
# ═══════════════════════════════════════════════

def calc_macd(closes) -> tuple:
    s = pd.Series(np.asarray(closes, dtype=float))
    ema_f = s.ewm(span=MACD_FAST, adjust=False).mean()
    ema_s = s.ewm(span=MACD_SLOW, adjust=False).mean()
    dif = ema_f - ema_s
    dea = dif.ewm(span=MACD_SIGNAL, adjust=False).mean()
    return dif.values, dea.values, (2 * (dif - dea)).values


def analyze_divergence(bis, closes) -> List[Dict[str, Any]]:
    """背驰：比较同方向相邻段（中间隔一段反向笔），价格创新高/低但 MACD 力度衰减。"""
    if len(bis) < 3:
        return []
    dif, _, _ = calc_macd(closes)
    out = []
    # 分别按方向分组，比较同方向相邻段
    for group in ([b for b in bis if b.type == Direction.Up],
                  [b for b in bis if b.type == Direction.Down]):
        for i in range(1, len(group)):
            pb, cb = group[i - 1], group[i]
            pa = sum(abs(dif[k]) for k in range(pb.start_idx, pb.end_idx + 1))
            ca = sum(abs(dif[k]) for k in range(cb.start_idx, cb.end_idx + 1))
            ratio = ca / max(pa, 1e-10)
            # 价格创新高/新低 + 力度衰减(面积比 < 阈值)
            if cb.type == Direction.Up and cb.end_fx.price > pb.end_fx.price and ratio < DIVERGENCE_RATIO:
                out.append({"type": "顶背驰", "date": cb.end_fx.dt.strftime("%Y-%m-%d"),
                            "price": round(cb.end_fx.price, 3), "ratio": round(ratio, 3),
                            "prev_pct": round(pb.change_pct, 2), "curr_pct": round(cb.change_pct, 2)})
            elif cb.type == Direction.Down and cb.end_fx.price < pb.end_fx.price and ratio < DIVERGENCE_RATIO:
                out.append({"type": "底背驰", "date": cb.end_fx.dt.strftime("%Y-%m-%d"),
                            "price": round(cb.end_fx.price, 3), "ratio": round(ratio, 3),
                            "prev_pct": round(pb.change_pct, 2), "curr_pct": round(cb.change_pct, 2)})
    return out


# ═══════════════════════════════════════════════
# 单标的分析
# ═══════════════════════════════════════════════

def analyze_one(category: str, key: str, name: str) -> Dict[str, Any]:
    df_daily = LOADERS[category](key)
    df_weekly = resample_weekly(df_daily)

    bars = [Bar(dt=row.Date, open=float(row.Open), high=float(row.High),
                low=float(row.Low), close=float(row.Close), vol=int(row.Volume))
            for row in df_weekly.itertuples()]

    res = chanlun_analyze(bars, min_bi_len=MIN_BI_LEN, min_zs_overlap=ZS_MIN_OVERLAP)

    closes = [b.close for b in res.std_bars]
    divs = analyze_divergence(res.bis, closes)

    last_close = df_weekly["Close"].iloc[-1]
    last_date = df_weekly["Date"].iloc[-1].strftime("%Y-%m-%d")

    # 历史位置
    hi = float(df_weekly["High"].max())
    lo = float(df_weekly["Low"].min())
    pct_pos = round((last_close - lo) / (hi - lo) * 100, 1) if hi > lo else 0.0

    # 最近10笔
    bis_recent = []
    for b in res.bis[-10:]:
        bis_recent.append({
            "dir": "UP" if b.type == Direction.Up else "DOWN",
            "start": b.start_fx.dt.strftime("%Y-%m-%d"),
            "end": b.end_fx.dt.strftime("%Y-%m-%d"),
            "start_price": round(b.start_fx.price, 3),
            "end_price": round(b.end_fx.price, 3),
            "change_pct": round(b.change_pct, 2),
            "power": round(b.power, 1),
            "bars": b.bar_count,
        })

    # 中枢
    zhongshus = []
    for z in res.zhongshus:
        zhongshus.append({
            "zg": round(z.zg, 3), "zd": round(z.zd, 3), "zz": round(z.zz, 3),
            "start": z.dt_start.strftime("%Y-%m-%d") if z.dt_start else None,
            "end": z.dt_end.strftime("%Y-%m-%d") if z.dt_end else None,
            "width_pct": round((z.zg - z.zd) / z.zd * 100, 2),
        })

    # 价格 vs 最新中枢
    pos_vs_zs = None
    if zhongshus:
        lz = zhongshus[-1]
        if last_close > lz["zg"]:
            pos_vs_zs = "上方"
        elif last_close < lz["zd"]:
            pos_vs_zs = "下方"
        else:
            pos_vs_zs = "内部"

    result = {
        "category": category, "key": key, "name": name,
        "data_range": f"{df_weekly['Date'].iloc[0].strftime('%Y-%m-%d')} ~ {last_date}",
        "raw_weekly": len(df_weekly),
        "std_count": res.std_count if hasattr(res, "std_count") else len(res.std_bars),
        "fractal_count": len(res.fractals),
        "bi_count": len(res.bis),
        "zhongshu_count": len(res.zhongshus),
        "trend": res.trend,
        "last_close": round(last_close, 3),
        "last_date": last_date,
        "hist_high": round(hi, 3), "hist_low": round(lo, 3), "pct_pos": pct_pos,
        "last_bi": bis_recent[-1] if bis_recent else None,
        "bis_recent": bis_recent,
        "zhongshus": zhongshus,
        "pos_vs_zs": pos_vs_zs,
        "divergences": divs,
    }
    return result


# ═══════════════════════════════════════════════
# 主流程
# ═══════════════════════════════════════════════

def main():
    cats = {"futures": FUTURES, "indices": INDICES, "stocks": STOCKS, "sectors": SECTORS}
    argv = sys.argv[1:]
    only = None
    if "--cat" in argv:
        only = argv[argv.index("--cat") + 1]

    os.makedirs(OUT_DIR, exist_ok=True)

    summary = []
    for cat, universe in cats.items():
        if only and cat != only:
            continue
        for key, name in universe.items():
            try:
                r = analyze_one(cat, key, name)
                path = os.path.join(OUT_DIR, f"{cat}_{key}.json")
                with open(path, "w", encoding="utf-8") as f:
                    json.dump(r, f, ensure_ascii=False, indent=2)
                summary.append(r)
                lb = r["last_bi"]["dir"] if r["last_bi"] else "?"
                dv = r["divergences"][-1]["type"] if r["divergences"] else "无"
                print(f"[OK] {cat:8s} {name:12s} 笔{r['bi_count']:3d} 中枢{r['zhongshu_count']:2d} "
                      f"走势{r['trend']:14s} 末笔{lb:4s} 背驰:{dv} 位置{r['pct_pos']:5.1f}% "
                      f"vs中枢={r['pos_vs_zs']}")
            except Exception as e:  # noqa: BLE001
                print(f"[FAIL] {cat:8s} {name:12s} -> {type(e).__name__}: {str(e)[:80]}")

    # 汇总表保存
    if summary:
        out = os.path.join(OUT_DIR, "_summary.json")
        with open(out, "w", encoding="utf-8") as f:
            json.dump(summary, f, ensure_ascii=False, indent=2)
        print(f"\n完成 {len(summary)} 个标的，结果目录: {OUT_DIR}")
        print(f"汇总: {out}")


if __name__ == "__main__":
    main()
