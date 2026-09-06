#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
白银 AG2612 破位监控（供 Monitor 工具后台运行）
  - 下破 15350（铁位）→ 周线底背驰失败，2 手无条件止损
  - 上破 16374（日线中枢上沿）→ 日线向上确认
只在「破位事件」输出一行（成为通知），避免噪音。非交易时段降低轮询频率。
数据源：ak.futures_zh_realtime(symbol='白银')（新浪实时）
"""
import akshare as ak
import time
import sys
from datetime import datetime

STOP_LINE = 15350    # 铁位（证伪线），下破无条件止损
BREAK_LINE = 16374   # 日线中枢上沿，上破确认向上

def in_trading_hours():
    """白银交易时段：日盘 09:00-15:00（含 10:15-10:30 休），夜盘 21:00-次日 02:30。"""
    now = datetime.now()
    hm = now.hour * 100 + now.minute
    if 900 <= hm <= 1500:      # 日盘（粗略，含午休）
        return True
    if hm >= 2100 or hm <= 230:  # 夜盘 21:00-02:30
        return True
    return False

def get_ag2612():
    """返回 AG2612 最新价；失败返回 None。"""
    try:
        df = ak.futures_zh_realtime(symbol='白银')
        row = df[df['symbol'] == 'AG2612']
        if row.empty:
            return None
        return float(row.iloc[0]['trade'])
    except Exception:
        return None

def main():
    sys.stdout.reconfigure(encoding='utf-8')
    price = get_ag2612()
    if price is None:
        print("⚠️ 无法获取 AG2612 行情，监控未启动")
        return
    print(f"🛰️ 白银 AG2612 监控已启动：现价 {price:.0f}｜止损线 {STOP_LINE}｜突破线 {BREAK_LINE}")

    last_state = 'below' if price <= STOP_LINE else ('above' if price >= BREAK_LINE else 'mid')

    while True:
        try:
            price = get_ag2612()
            if price is None:
                time.sleep(30)
                continue

            if price <= STOP_LINE:
                if last_state != 'below':
                    print(f"🔴 白银 AG2612 已破铁位 {STOP_LINE}！现价 {price:.0f}，周线底背驰失败，2 手 @16530.5 无条件止损")
                    last_state = 'below'
            elif price >= BREAK_LINE:
                if last_state != 'above':
                    print(f"🟢 白银 AG2612 突破 {BREAK_LINE}！现价 {price:.0f}，日线向上确认，加仓等待开始兑现")
                    last_state = 'above'
            else:
                last_state = 'mid'

            # 交易时段 30s，非交易时段 5min
            time.sleep(30 if in_trading_hours() else 300)
        except Exception:
            time.sleep(30)

if __name__ == '__main__':
    main()
