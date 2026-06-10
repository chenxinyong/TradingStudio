"""
直接从 SHFE 网站获取有色金属合约规格，生成 .md 文件

两种方法：
1. 尝试从页面嵌入的 JS 数据中提取 JSON
2. 使用 Python requests + BeautifulSoup 解析 HTML 表格
"""
import sys
sys.stdout.reconfigure(encoding='utf-8')

import requests
import re
import json
from pathlib import Path
from bs4 import BeautifulSoup

DOCS_DIR = Path(r"C:\Works\ClaudeCode\TradingStudio\docs\contracts")

# 品种配置
TARGETS = [
    ("铝", "AL", "al_f"),
    ("锌", "ZN", "zn_f"),
    ("铅", "PB", "pb_f"),
    ("镍", "NI", "ni_f"),
    ("锡", "SN", "sn_f"),
    ("氧化铝", "AO", "ao_f"),
]

HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
}


def fetch_page_data(session, product_code):
    """尝试提取页面中嵌入的 JSON 数据"""
    url = f"https://www.shfe.com.cn/products/futures/metal/nonferrousmetal/{product_code}/"
    r = session.get(url, timeout=15, headers=HEADERS)
    if r.status_code != 200:
        return None

    content = r.text

    # Method 1: Look for JavaScript variable assignment of contract data
    patterns = [
        r'pageList\s*=\s*(\[.*?\])\s*;',           # Vue data
        r'productInfo\s*=\s*(\{.*?\})\s*;',
        r'contractData\s*=\s*(\{.*?\})\s*;',
        r'"contractInfo"\s*:\s*(\{.*?\})\s*[,}]',
        r'"product"\s*:\s*(\{.*?\})\s*[,}]',
        r'var\s+pageList\s*=\s*(\[.*?\])',         # var assignment
    ]

    for pattern in patterns:
        m = re.search(pattern, content, re.DOTALL)
        if m:
            try:
                data = json.loads(m.group(1))
                return data
            except:
                pass

    # Method 2: Extract all script contents and search
    soup = BeautifulSoup(content, 'html.parser')
    for script in soup.find_all('script'):
        if script.string and len(script.string) > 100:
            for pattern in patterns:
                m = re.search(pattern, script.string, re.DOTALL)
                if m:
                    try:
                        data = json.loads(m.group(1))
                        return data
                    except:
                        pass

    # Method 3: Search for specific field=value patterns in JS
    field_patterns = {
        "Product": r'Product["\']?\s*[:=]\s*["\']([^"\']+)',
        "ContractSize": r'ContractSize["\']?\s*[:=]\s*["\']([^"\']+)',
        "MinimumPriceFluctuation": r'MinimumPriceFluctuation["\']?\s*[:=]\s*["\']([^"\']+)',
    }

    return None


def scrape_table_data(session, product_code):
    """直接解析 HTML 表格"""
    url = f"https://www.shfe.com.cn/products/futures/metal/nonferrousmetal/{product_code}/"
    r = session.get(url, timeout=15, headers=HEADERS)
    if r.status_code != 200:
        return {}

    soup = BeautifulSoup(r.text, 'html.parser')

    # Extract all text
    text = soup.get_text()
    lines = [l.strip() for l in text.split('\n') if l.strip() and len(l.strip()) > 3]

    # Known field-value pairs for SHFE metals
    # These are commonly found in the page HTML
    specs = {}
    key_fields = [
        "交易品种", "交易单位", "报价单位", "最小变动价位",
        "涨跌停板幅度", "合约月份", "交易时间", "最后交易日",
        "交割日期", "交割品级", "交割地点", "最低交易保证金",
        "交割方式", "交割单位", "交易代码", "上市交易所",
    ]

    for line in lines:
        for key in key_fields:
            if key in line and key not in specs:
                # Find the value after the key
                idx = line.find(key)
                rest = line[idx + len(key):].strip()
                rest = rest.lstrip('：:').strip()
                # May have Chinese comma/cjk chars as separators
                if rest:
                    # Clean: remove repeated field names
                    for k2 in key_fields:
                        rest = rest.split(k2)[0]
                    specs[key] = rest.strip()
                break

    return specs


def generate_markdown(name, code, product_code, specs):
    """生成 .md 文件"""
    url = f"https://www.shfe.com.cn/products/futures/metal/nonferrousmetal/{product_code}/"

    def get_val(key):
        v = specs.get(key, "—")
        if not v:
            v = "—"
        return v

    content = f"""---
title: "{name}"
source: "{url}"
created: 2026-06-10
tags:
  - "clippings"
  - "SHFE"
  - "合约规格"
  - "有色金属"
---

期货

[期权](https://www.shfe.com.cn/products/option/nonferrousmetal/{product_code.replace('_f','_o')}/)

| 交易品种    | {get_val("交易品种")} |
| ------- | -------------------------------------------------------------------------- |
| 交易单位    | {get_val("交易单位")} |
| 报价单位    | {get_val("报价单位")} |
| 最小变动价位  | {get_val("最小变动价位")} |
| 涨跌停板幅度  | {get_val("涨跌停板幅度")} |
| 合约月份    | {get_val("合约月份")} |
| 交易时间    | {get_val("交易时间")} |
| 最后交易日   | {get_val("最后交易日")} |
| 交割日期    | {get_val("交割日期")} |
| 交割品级    | {get_val("交割品级")} |
| 交割地点    | {get_val("交割地点")} |
| 最低交易保证金 | {get_val("最低交易保证金")} |
| 交割方式    | {get_val("交割方式")} |
| 交割单位    | {get_val("交割单位")} |
| 交易代码    | {code} |
| 上市交易所   | {get_val("上市交易所")} |

---

> 数据来源: {url}
> 下载日期: 2026-06-10
"""
    return content


def main():
    print("=== 从 SHFE 网站获取合约规格 ===\n")
    session = requests.Session()

    for name, code, product_code in TARGETS:
        print(f"{name} ({code}):")

        # Try to extract data
        specs = scrape_table_data(session, product_code)
        found = sum(1 for v in specs.values() if v and v != "—")
        print(f"  提取到 {found}/16 个字段: {list(specs.keys())}")

        # Generate md
        content = generate_markdown(name, code, product_code, specs)
        filepath = DOCS_DIR / f"{name}.md"
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(content)
        print(f"  → {filepath}")

    print(f"\n完成！")


if __name__ == "__main__":
    main()
