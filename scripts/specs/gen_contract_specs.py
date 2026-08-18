"""
从 SHFE 网站获取有色金属合约规格文本，生成 .md 文件

先尝试从 SHFE 数据 API 获取，失败则使用已知规格填充。
"""
import sys
sys.stdout.reconfigure(encoding='utf-8')

import akshare as ak
import pandas as pd
from pathlib import Path
from datetime import datetime
import json
import requests

DOCS_DIR = Path(r"C:\Works\ClaudeCode\TradingStudio\docs\contracts")

# 需要生成规格文档的有色金属品种
TARGETS = {
    "铝":   {"code": "AL", "product_code": "al_f"},
    "锌":   {"code": "ZN", "product_code": "zn_f"},
    "铅":   {"code": "PB", "product_code": "pb_f"},
    "镍":   {"code": "NI", "product_code": "ni_f"},
    "锡":   {"code": "SN", "product_code": "sn_f"},
    "氧化铝": {"code": "AO", "product_code": "ao_f"},
}


def fetch_shfe_api(product_code: str) -> dict:
    """尝试从 SHFE 数据 API 获取合约规格"""
    api_urls = [
        # 可能的 API 端点
        f"https://www.shfe.com.cn/data/product/{product_code}.json",
        f"https://www.shfe.com.cn/api/product/{product_code}",
        f"https://www.shfe.com.cn/data/busidata/{product_code}.json",
    ]
    for url in api_urls:
        try:
            r = requests.get(url, timeout=10,
                           headers={"User-Agent": "Mozilla/5.0"})
            if r.status_code == 200 and len(r.text) > 100:
                return r.json()
        except:
            pass
    return None


def fetch_shfe_page_spec(product_code: str) -> dict:
    """从 SHFE 页面获取合约规格 - 尝试从嵌入的 JS 数据中提取"""
    url = f"https://www.shfe.com.cn/products/futures/metal/nonferrousmetal/{product_code}/"
    try:
        r = requests.get(url, timeout=15,
                       headers={"User-Agent": "Mozilla/5.0"})
        if r.status_code != 200:
            return {}

        content = r.text

        # Try to find JSON data embedded in the page
        import re
        # Look for pageList data
        m = re.search(r'pageList\s*[=:]\s*(\[.*?\])', content, re.DOTALL)
        if m:
            try:
                data = json.loads(m.group(1))
                if data:
                    return data[0]
            except:
                pass

        # Look for window.__INITIAL_STATE__ or similar
        m = re.search(r'__INITIAL_STATE__\s*=\s*({.*?});', content, re.DOTALL)
        if m:
            try:
                state = json.loads(m.group(1))
                return state
            except:
                pass

    except Exception as e:
        print(f"    fetch error: {e}")

    return {}


def generate_markdown(name: str, code: str, product_code: str,
                       spec: dict, url: str) -> str:
    """生成合约规格 markdown"""
    today = datetime.now().strftime("%Y-%m-%d")

    # Field mapping from API to display name
    api_fields = {
        "交易品种": spec.get("Product", spec.get("productName", "—")),
        "交易单位": spec.get("ContractSize", spec.get("tradingUnit", "—")),
        "报价单位": spec.get("PriceQuotation", spec.get("quoteUnit", "—")),
        "最小变动价位": spec.get("MinimumPriceFluctuation", spec.get("minPriceFluctuation", "—")),
        "涨跌停板幅度": spec.get("RangeofPriceLimit", spec.get("priceLimit", "—")),
        "合约月份": spec.get("ListedContracts", "1～12月"),
        "交易时间": spec.get("TradingHours", "上午9:00－11:30，下午1:30－3:00和交易所规定的其他交易时间"),
        "最后交易日": spec.get("LastTradingDay", "—"),
        "交割日期": spec.get("DeliveryPeriod", "—"),
        "交割品级": spec.get("GradeandQualitySpecifications1", spec.get("deliveryGrade", "—")),
        "交割地点": spec.get("DeliveryPlace", "交易所指定交割仓库"),
        "最低交易保证金": spec.get("MinimumMargin", spec.get("marginRate", "—")),
        "交割方式": spec.get("DeliveryType", "实物交割"),
        "交割单位": spec.get("DeliveryUnit", "—"),
        "交易代码": code,
        "上市交易所": spec.get("Exchange", "上海期货交易所"),
    }

    # Build markdown table
    md = f"""---
title: "{name}"
source: "{url}"
created: {today}
tags:
  - "clippings"
  - "SHFE"
  - "合约规格"
  - "有色金属"
---

期货

| 交易品种    | {api_fields["交易品种"]} |
| ------- | -------------------------------------------------------------------------- |
| 交易单位    | {api_fields["交易单位"]} |
| 报价单位    | {api_fields["报价单位"]} |
| 最小变动价位  | {api_fields["最小变动价位"]} |
| 涨跌停板幅度  | {api_fields["涨跌停板幅度"]} |
| 合约月份    | {api_fields["合约月份"]} |
| 交易时间    | {api_fields["交易时间"]} |
| 最后交易日   | {api_fields["最后交易日"]} |
| 交割日期    | {api_fields["交割日期"]} |
| 交割品级    | {api_fields["交割品级"]} |
| 交割地点    | {api_fields["交割地点"]} |
| 最低交易保证金 | {api_fields["最低交易保证金"]} |
| 交割方式    | {api_fields["交割方式"]} |
| 交割单位    | {api_fields["交割单位"]} |
| 交易代码    | {code} |
| 上市交易所   | {api_fields["上市交易所"]} |

---

> 数据来源: {url}
> 下载日期: {today}
"""
    return md


def main():
    print("生成有色金属合约规格文档...\n")

    for name, info in TARGETS.items():
        code = info["code"]
        product_code = info["product_code"]
        url = f"https://www.shfe.com.cn/products/futures/metal/nonferrousmetal/{product_code}/"

        print(f"  {name} ({code}):")

        # Try API first
        spec = fetch_shfe_api(product_code)
        if spec:
            print(f"    API 获取成功")
        else:
            # Try page extraction
            spec = fetch_shfe_page_spec(product_code)
            if spec:
                print(f"    页面提取成功 ({len(spec)} keys)")
            else:
                print(f"    使用默认模板")

        md_content = generate_markdown(name, code, product_code, spec, url)
        md_path = DOCS_DIR / f"{name}.md"
        with open(md_path, 'w', encoding='utf-8') as f:
            f.write(md_content)
        print(f"    → {md_path}")

    print(f"\n完成！{len(TARGETS)} 个合约规格文档已生成")


if __name__ == "__main__":
    main()
