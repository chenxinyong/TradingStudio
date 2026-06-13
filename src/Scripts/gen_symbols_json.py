"""
Generate symbols.json — 75 futures symbols with trading rules.
"""
import json
from datetime import date

# (name, exchange, category, delivery, unit, unit_name, tick_size, margin, months)
DATA = [
    # ========== SHFE 上海期货交易所 (18) ==========
    ("铜",        "SHFE", "有色金属", "PHYSICAL", 5,    "吨",   10,  0.08, "1～12月"),
    ("铝",        "SHFE", "有色金属", "PHYSICAL", 5,    "吨",   5,   0.08, "1～12月"),
    ("锌",        "SHFE", "有色金属", "PHYSICAL", 5,    "吨",   5,   0.08, "1～12月"),
    ("铅",        "SHFE", "有色金属", "PHYSICAL", 5,    "吨",   5,   0.08, "1～12月"),
    ("镍",        "SHFE", "有色金属", "PHYSICAL", 1,    "吨",   10,  0.10, "1～12月"),
    ("锡",        "SHFE", "有色金属", "PHYSICAL", 1,    "吨",   10,  0.10, "1～12月"),
    ("黄金",      "SHFE", "贵金属",   "PHYSICAL", 1000, "克",   0.02, 0.08, "双月(2,4,6,8,10,12);最近三期连续"),
    ("白银",      "SHFE", "贵金属",   "PHYSICAL", 15,   "千克",  1,    0.08, "1～12月"),
    ("螺纹钢",    "SHFE", "黑色金属", "PHYSICAL", 10,   "吨",   1,    0.07, "1～12月"),
    ("线材",      "SHFE", "黑色金属", "PHYSICAL", 10,   "吨",   1,    0.07, "1～12月"),
    ("热轧卷板",  "SHFE", "黑色金属", "PHYSICAL", 10,   "吨",   1,    0.07, "1～12月"),
    ("不锈钢",    "SHFE", "黑色金属", "PHYSICAL", 5,    "吨",   5,    0.07, "1～12月"),
    ("石油沥青",  "SHFE", "化工",     "PHYSICAL", 10,   "吨",   1,    0.08, "1～12月"),
    ("天然橡胶",  "SHFE", "化工",     "PHYSICAL", 10,   "吨",   5,    0.08, "1,5,9,11月"),
    ("纸浆",      "SHFE", "化工",     "PHYSICAL", 10,   "吨",   2,    0.08, "1～12月"),
    ("燃料油",    "SHFE", "能源",     "PHYSICAL", 10,   "吨",   1,    0.10, "1～12月"),
    ("氧化铝",    "SHFE", "有色金属", "PHYSICAL", 20,   "吨",   1,    0.08, "1～12月"),
    ("丁二烯橡胶","SHFE", "化工",     "PHYSICAL", 5,    "吨",   5,    0.08, "1～12月"),

    # ========== INE 上海能源 (5) ==========
    ("原油",          "INE", "能源",     "PHYSICAL", 1000, "桶", 0.1, 0.10, "最近12个月;之后季度"),
    ("低硫燃料油",    "INE", "能源",     "PHYSICAL", 10,   "吨", 1,   0.10, "1～12月"),
    ("铜(BC)",        "INE", "有色金属", "PHYSICAL", 5,    "吨", 10,  0.08, "1～12月"),
    ("20号胶",        "INE", "化工",     "PHYSICAL", 10,   "吨", 5,   0.08, "1～12月"),
    ("SCFIS欧线",     "INE", "能源",     "CASH",     50,   "点", 0.1, 0.12, "双月(2,4,6,8,10,12)"),

    # ========== DCE 大连商品交易所 (21) ==========
    ("黄玉米",          "DCE", "农产品", "PHYSICAL", 10,   "吨",   1,   0.05, "1,3,5,7,9,11"),
    ("玉米淀粉",        "DCE", "农产品", "PHYSICAL", 10,   "吨",   1,   0.05, "1,3,5,7,9,11"),
    ("黄大豆1号",       "DCE", "农产品", "PHYSICAL", 10,   "吨",   1,   0.05, "1,3,5,7,9,11"),
    ("黄大豆2号",       "DCE", "农产品", "PHYSICAL", 10,   "吨",   1,   0.05, "1～12月"),
    ("豆粕",            "DCE", "农产品", "PHYSICAL", 10,   "吨",   1,   0.07, "1,3,5,7,8,9,11,12"),
    ("豆油",            "DCE", "农产品", "PHYSICAL", 10,   "吨",   2,   0.07, "1,3,5,7,8,9,11,12"),
    ("棕榈油",          "DCE", "农产品", "PHYSICAL", 10,   "吨",   2,   0.08, "1～12月"),
    ("线型低密度聚乙烯","DCE", "化工",   "PHYSICAL", 5,    "吨",   1,   0.07, "1～12月"),
    ("聚氯乙烯",        "DCE", "化工",   "PHYSICAL", 5,    "吨",   1,   0.07, "1～12月"),
    ("聚丙烯",          "DCE", "化工",   "PHYSICAL", 5,    "吨",   1,   0.07, "1～12月"),
    ("苯乙烯",          "DCE", "化工",   "PHYSICAL", 5,    "吨",   1,   0.08, "1～12月"),
    ("乙二醇",          "DCE", "化工",   "PHYSICAL", 10,   "吨",   1,   0.08, "1～12月"),
    ("液化石油气",      "DCE", "能源",   "PHYSICAL", 20,   "吨",   1,   0.08, "1～12月"),
    ("焦炭",            "DCE", "黑色金属","PHYSICAL",100,  "吨",   0.5, 0.15, "1～12月"),
    ("焦煤",            "DCE", "黑色金属","PHYSICAL",60,   "吨",   0.5, 0.15, "1～12月"),
    ("铁矿石",          "DCE", "黑色金属","PHYSICAL",100,  "吨",   0.5, 0.10, "1～12月"),
    ("粳米",            "DCE", "农产品", "PHYSICAL", 10,   "吨",   1,   0.05, "1～12月"),
    ("纤维板",          "DCE", "化工",   "PHYSICAL", 10,   "立方米",0.5, 0.10, "1～12月"),
    ("胶合板",          "DCE", "化工",   "PHYSICAL", 500,  "张",   0.05,0.10, "1～12月"),
    ("鸡蛋",            "DCE", "农产品", "PHYSICAL", 5,    "吨",   1,   0.08, "1～12月"),
    ("生猪",            "DCE", "农产品", "PHYSICAL", 16,   "吨",   1,   0.12, "1,3,5,7,9,11"),

    # ========== CZCE 郑州商品交易所 (20) ==========
    ("棉花",      "CZCE", "农产品", "PHYSICAL", 5,  "吨", 5,  0.07, "1,3,5,7,9,11"),
    ("棉纱",      "CZCE", "农产品", "PHYSICAL", 5,  "吨", 5,  0.07, "1～12月"),
    ("白糖",      "CZCE", "农产品", "PHYSICAL", 10, "吨", 1,  0.07, "1,3,5,7,9,11"),
    ("PTA",       "CZCE", "化工",   "PHYSICAL", 5,  "吨", 2,  0.07, "1～12月"),
    ("甲醇",      "CZCE", "化工",   "PHYSICAL", 10, "吨", 1,  0.08, "1～12月"),
    ("玻璃",      "CZCE", "化工",   "PHYSICAL", 20, "吨", 1,  0.09, "1～12月"),
    ("纯碱",      "CZCE", "化工",   "PHYSICAL", 20, "吨", 1,  0.09, "1～12月"),
    ("硅铁",      "CZCE", "黑色金属","PHYSICAL", 5,  "吨", 2,  0.08, "1～12月"),
    ("锰硅",      "CZCE", "黑色金属","PHYSICAL", 5,  "吨", 2,  0.08, "1～12月"),
    ("尿素",      "CZCE", "化工",   "PHYSICAL", 20, "吨", 1,  0.08, "1～12月"),
    ("短纤",      "CZCE", "化工",   "PHYSICAL", 5,  "吨", 2,  0.08, "1～12月"),
    ("花生",      "CZCE", "农产品", "PHYSICAL", 5,  "吨", 2,  0.08, "1,3,5,7,9,11"),
    ("菜粕",      "CZCE", "农产品", "PHYSICAL", 10, "吨", 1,  0.07, "1,3,5,7,8,9,11"),
    ("菜油",      "CZCE", "农产品", "PHYSICAL", 10, "吨", 2,  0.07, "1,3,5,7,9,11"),
    ("烧碱",      "CZCE", "化工",   "PHYSICAL", 30, "吨", 1,  0.08, "1～12月"),
    ("瓶片",      "CZCE", "化工",   "PHYSICAL", 5,  "吨", 2,  0.08, "1～12月"),
    ("苹果",      "CZCE", "农产品", "PHYSICAL", 10, "吨", 1,  0.10, "1,3,5,7,10,11,12"),
    ("红枣",      "CZCE", "农产品", "PHYSICAL", 5,  "吨", 5,  0.10, "1,3,5,7,9,11"),
    ("对二甲苯",  "CZCE", "化工",   "PHYSICAL", 5,  "吨", 2,  0.09, "1～12月"),
    ("多晶硅",    "CZCE", "新能源", "PHYSICAL", 3,  "吨", 5,  0.09, "1～12月"),

    # ========== CFFEX 中国金融期货交易所 (8) ==========
    ("沪深300",      "CFFEX", "股指", "CASH", 300,   "元/点", 0.2,  0.12,  "当月,下月,后两个季月"),
    ("中证500",      "CFFEX", "股指", "CASH", 200,   "元/点", 0.2,  0.14,  "当月,下月,后两个季月"),
    ("上证50",       "CFFEX", "股指", "CASH", 300,   "元/点", 0.2,  0.12,  "当月,下月,后两个季月"),
    ("中证1000",     "CFFEX", "股指", "CASH", 200,   "元/点", 0.2,  0.15,  "当月,下月,后两个季月"),
    ("2年期国债",    "CFFEX", "利率", "CASH", 20000, "元/点", 0.005,0.005, "季月(3,6,9,12)"),
    ("5年期国债",    "CFFEX", "利率", "CASH", 10000, "元/点", 0.005,0.01,  "季月(3,6,9,12)"),
    ("10年期国债",   "CFFEX", "利率", "CASH", 10000, "元/点", 0.005,0.02,  "季月(3,6,9,12)"),
    ("30年期国债",   "CFFEX", "利率", "CASH", 10000, "元/点", 0.01, 0.035, "季月(3,6,9,12)"),

    # ========== GFEX 广州期货交易所 (3) ==========
    ("工业硅",  "GFEX", "新能源", "PHYSICAL", 5, "吨", 5,  0.09, "1～12月"),
    ("碳酸锂",  "GFEX", "新能源", "PHYSICAL", 1, "吨", 50, 0.09, "1～12月"),
    ("多晶硅",  "GFEX", "新能源", "PHYSICAL", 3, "吨", 5,  0.09, "1～12月"),
]

# CTP symbol codes (derived from name)
CODES = {
    "铜":"cu","铝":"al","锌":"zn","铅":"pb","镍":"ni","锡":"sn","黄金":"au","白银":"ag",
    "螺纹钢":"rb","线材":"wr","热轧卷板":"hc","不锈钢":"ss","石油沥青":"bu","天然橡胶":"ru",
    "纸浆":"sp","燃料油":"fu","氧化铝":"ao","丁二烯橡胶":"br",
    "原油":"sc","低硫燃料油":"lu","铜(BC)":"bc","20号胶":"nr","SCFIS欧线":"ec",
    "黄玉米":"c","玉米淀粉":"cs","黄大豆1号":"a","黄大豆2号":"b","豆粕":"m","豆油":"y",
    "棕榈油":"p","线型低密度聚乙烯":"l","聚氯乙烯":"v","聚丙烯":"pp",
    "苯乙烯":"eb","乙二醇":"eg","液化石油气":"pg","焦炭":"j","焦煤":"jm","铁矿石":"i",
    "粳米":"rr","纤维板":"fb","胶合板":"bb","鸡蛋":"jd","生猪":"lh",
    "棉花":"CF","棉纱":"CY","白糖":"SR","PTA":"TA","甲醇":"MA","玻璃":"FG","纯碱":"SA",
    "硅铁":"SF","锰硅":"SM","尿素":"UR","短纤":"PF","花生":"PK","菜粕":"RM","菜油":"OI",
    "烧碱":"SH","瓶片":"PR","苹果":"AP","红枣":"CJ","对二甲苯":"PX","多晶硅":"PS",
    "沪深300":"IF","中证500":"IC","上证50":"IH","中证1000":"IM",
    "2年期国债":"TS","5年期国债":"TF","10年期国债":"T","30年期国债":"TL",
    "工业硅":"si","碳酸锂":"lc","多晶硅":"ps",
}

# Trading hours — 日盘 + 夜盘时段
# All commodity futures day: 09:00-10:15, 10:30-11:30, 13:30-15:00
# CFFEX (financial): 09:30-11:30, 13:00-15:00 (IF/IC/IH/IM), 13:00-15:15 (国债)
# Night session varies by contract
DAY_ALL   = "09:00-10:15,10:30-11:30,13:30-15:00"
NIGHT_23  = "; 夜盘:21:00-23:00"
NIGHT_01  = "; 夜盘:21:00-01:00"
NIGHT_0230 = "; 夜盘:21:00-02:30"
NO_NIGHT  = ""
CFFEX_DAY_INDEX = "09:30-11:30,13:00-15:00"
CFFEX_DAY_BOND  = "09:30-11:30,13:00-15:15"

# contract_name -> (tradingHours, priceLimitPct)
RULES = {
    # SHFE
    "铜":         (DAY_ALL + NIGHT_01,   0.10),
    "铝":         (DAY_ALL + NIGHT_01,   0.10),
    "锌":         (DAY_ALL + NIGHT_01,   0.10),
    "铅":         (DAY_ALL + NIGHT_01,   0.10),
    "镍":         (DAY_ALL + NIGHT_01,   0.10),
    "锡":         (DAY_ALL + NIGHT_01,   0.10),
    "黄金":       (DAY_ALL + NIGHT_0230, 0.08),
    "白银":       (DAY_ALL + NIGHT_0230, 0.08),
    "螺纹钢":     (DAY_ALL + NIGHT_23,   0.07),
    "线材":       (DAY_ALL + NIGHT_23,   0.07),
    "热轧卷板":   (DAY_ALL + NIGHT_23,   0.07),
    "不锈钢":     (DAY_ALL + NIGHT_23,   0.07),
    "石油沥青":   (DAY_ALL + NIGHT_23,   0.08),
    "天然橡胶":   (DAY_ALL + NIGHT_23,   0.08),
    "纸浆":       (DAY_ALL + NIGHT_23,   0.08),
    "燃料油":     (DAY_ALL + NIGHT_23,   0.10),
    "氧化铝":     (DAY_ALL + NIGHT_23,   0.08),
    "丁二烯橡胶": (DAY_ALL + NIGHT_23,   0.08),
    # INE
    "原油":       (DAY_ALL + NIGHT_0230, 0.10),
    "低硫燃料油": (DAY_ALL + NIGHT_23,   0.10),
    "铜(BC)":     (DAY_ALL + NIGHT_01,   0.08),
    "20号胶":     (DAY_ALL + NIGHT_23,   0.08),
    "SCFIS欧线":  (DAY_ALL + NIGHT_0230, 0.12),
    # DCE
    "黄玉米":         (DAY_ALL + NO_NIGHT,  0.05),
    "玉米淀粉":       (DAY_ALL + NO_NIGHT,  0.05),
    "黄大豆1号":      (DAY_ALL + NIGHT_23,  0.05),
    "黄大豆2号":      (DAY_ALL + NIGHT_23,  0.05),
    "豆粕":           (DAY_ALL + NIGHT_23,  0.07),
    "豆油":           (DAY_ALL + NIGHT_23,  0.07),
    "棕榈油":         (DAY_ALL + NIGHT_23,  0.08),
    "线型低密度聚乙烯":(DAY_ALL + NIGHT_23,  0.07),
    "聚氯乙烯":       (DAY_ALL + NIGHT_23,  0.07),
    "聚丙烯":         (DAY_ALL + NIGHT_23,  0.07),
    "苯乙烯":         (DAY_ALL + NIGHT_23,  0.08),
    "乙二醇":         (DAY_ALL + NIGHT_23,  0.08),
    "液化石油气":     (DAY_ALL + NIGHT_23,  0.08),
    "焦炭":           (DAY_ALL + NIGHT_23,  0.15),
    "焦煤":           (DAY_ALL + NIGHT_23,  0.15),
    "铁矿石":         (DAY_ALL + NIGHT_23,  0.10),
    "粳米":           (DAY_ALL + NO_NIGHT,  0.05),
    "纤维板":         (DAY_ALL + NO_NIGHT,  0.10),
    "胶合板":         (DAY_ALL + NO_NIGHT,  0.10),
    "鸡蛋":           (DAY_ALL + NO_NIGHT,  0.08),
    "生猪":           (DAY_ALL + NO_NIGHT,  0.12),
    # CZCE
    "棉花":     (DAY_ALL + NIGHT_23, 0.07),
    "棉纱":     (DAY_ALL + NIGHT_23, 0.07),
    "白糖":     (DAY_ALL + NIGHT_23, 0.07),
    "PTA":      (DAY_ALL + NIGHT_23, 0.07),
    "甲醇":     (DAY_ALL + NIGHT_23, 0.08),
    "玻璃":     (DAY_ALL + NIGHT_23, 0.09),
    "纯碱":     (DAY_ALL + NIGHT_23, 0.09),
    "硅铁":     (DAY_ALL + NIGHT_23, 0.08),
    "锰硅":     (DAY_ALL + NIGHT_23, 0.08),
    "尿素":     (DAY_ALL + NIGHT_23, 0.08),
    "短纤":     (DAY_ALL + NIGHT_23, 0.08),
    "花生":     (DAY_ALL + NIGHT_23, 0.08),
    "菜粕":     (DAY_ALL + NIGHT_23, 0.07),
    "菜油":     (DAY_ALL + NIGHT_23, 0.07),
    "烧碱":     (DAY_ALL + NIGHT_23, 0.08),
    "瓶片":     (DAY_ALL + NIGHT_23, 0.08),
    "苹果":     (DAY_ALL + NO_NIGHT,  0.10),
    "红枣":     (DAY_ALL + NO_NIGHT,  0.10),
    "对二甲苯": (DAY_ALL + NIGHT_23, 0.09),
    "多晶硅":   (DAY_ALL + NIGHT_23, 0.09),
    # CFFEX
    "沪深300":    (CFFEX_DAY_INDEX, 0.10),
    "中证500":    (CFFEX_DAY_INDEX, 0.10),
    "上证50":     (CFFEX_DAY_INDEX, 0.10),
    "中证1000":   (CFFEX_DAY_INDEX, 0.10),
    "2年期国债":  (CFFEX_DAY_BOND,  0.005),
    "5年期国债":  (CFFEX_DAY_BOND,  0.012),
    "10年期国债": (CFFEX_DAY_BOND,  0.02),
    "30年期国债": (CFFEX_DAY_BOND,  0.035),
    # GFEX
    "工业硅": (DAY_ALL + NIGHT_23, 0.09),
    "碳酸锂": (DAY_ALL + NIGHT_23, 0.09),
    "多晶硅": (DAY_ALL + NIGHT_23, 0.09),
}

def main():
    symbols = []
    for i, (name, ex, cat, delivery, unit, unit_name, tick, margin, months) in enumerate(DATA, 1):
        code = CODES.get(name, "??")
        hours, limit = RULES.get(name, ("", 0.10))
        symbols.append({
            "id": i,
            "exchange": ex,
            "code": code,
            "name": name,
            "category": cat,
            "deliveryType": delivery,
            "tradingUnit": unit,
            "unitName": unit_name,
            "tickSize": tick,
            "tickValue": round(tick * unit, 4),
            "priceLimitPct": limit,
            "marginRate": margin,
            "months": months,
            "tradingHours": hours,
        })

    result = {"generatedAt": date.today().isoformat(), "symbols": symbols}
    path = "src/TradingStudio/symbols.json"
    with open(path, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=2)
    print(f"Generated {path}  ({len(symbols)} symbols)")

if __name__ == "__main__":
    main()
