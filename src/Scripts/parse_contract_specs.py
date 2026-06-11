"""
从 SHFE 网站下载的 HTML 页面中提取合约规格，生成 .md 文件
"""
from bs4 import BeautifulSoup
import re
import sys
import os
from pathlib import Path

DOCS_DIR = Path(r"C:\Works\ClaudeCode\TradingStudio\docs\contracts")

# 品种配置: (文件名前缀, 品种中文名, 交易代码, SHFE产品代码)
PRODUCTS = [
    ("铝", "铝", "AL", "al_f"),
    ("锌", "锌", "ZN", "zn_f"),
    ("铅", "铅", "PB", "pb_f"),
    ("镍", "镍", "NI", "ni_f"),
    ("锡", "锡", "SN", "sn_f"),
    ("氧化铝", "氧化铝", "AO", "ao_f"),
]


def extract_spec_from_html(html_path):
    """从 SHFE 合约页面 HTML 中提取规格表"""
    with open(html_path, 'r', encoding='utf-8') as f:
        html = f.read()

    soup = BeautifulSoup(html, 'html.parser')
    text = soup.get_text()

    # SHFE contract pages have a standard structure
    # Key fields to extract
    fields = {
        "交易品种": "",
        "交易单位": "",
        "报价单位": "",
        "最小变动价位": "",
        "涨跌停板幅度": "",
        "合约月份": "",
        "交易时间": "",
        "最后交易日": "",
        "交割日期": "",
        "交割品级": "",
        "交割地点": "",
        "最低交易保证金": "",
        "交割方式": "",
        "交割单位": "",
        "交易代码": "",
        "上市交易所": "",
    }

    # Find all text lines and match patterns
    lines = [l.strip() for l in text.split('\n') if l.strip() and len(l.strip()) > 2]

    for key in fields:
        for line in lines:
            if line.startswith(key):
                # Extract value after the key
                val = line[len(key):].strip()
                # Clean up
                val = val.lstrip('：').lstrip(':').strip()
                if val:
                    fields[key] = val
                break

    return fields


def generate_markdown(name, code, product_code, fields):
    """生成与铜.md 格式一致的 markdown"""
    today = "2026-06-10"
    url = f"https://www.shfe.com.cn/products/futures/metal/nonferrousmetal/{product_code}/"

    md = f"""---
title: "{name}"
source: "{url}"
created: {today}
tags:
  - "clippings"
  - "SHFE"
  - "合约规格"
---
期货

| 交易品种    | {fields.get('交易品种', '—')} |
| ------- | -------------------------------------------------------------------------- |
| 交易单位    | {fields.get('交易单位', '—')} |
| 报价单位    | {fields.get('报价单位', '—')} |
| 最小变动价位  | {fields.get('最小变动价位', '—')} |
| 涨跌停板幅度  | {fields.get('涨跌停板幅度', '—')} |
| 合约月份    | {fields.get('合约月份', '—')} |
| 交易时间    | {fields.get('交易时间', '—')} |
| 最后交易日   | {fields.get('最后交易日', '—')} |
| 交割日期    | {fields.get('交割日期', '—')} |
| 交割品级    | {fields.get('交割品级', '—')} |
| 交割地点    | {fields.get('交割地点', '—')} |
| 最低交易保证金 | {fields.get('最低交易保证金', '—')} |
| 交割方式    | {fields.get('交割方式', '—')} |
| 交割单位    | {fields.get('交割单位', '—')} |
| 交易代码    | {code} |
| 上市交易所   | {fields.get('上市交易所', '上海期货交易所')} |

---

> 数据来源: {url}
> 下载日期: {today}
"""
    return md


def main():
    for name, chinese_name, code, product_code in PRODUCTS:
        html_path = DOCS_DIR / f"{name}.html"
        md_path = DOCS_DIR / f"{name}.md"

        if not html_path.exists():
            print(f"  {name}: HTML 不存在，跳过")
            continue

        print(f"处理: {name} ({code})")
        fields = extract_spec_from_html(html_path)

        # Print extracted fields for verification
        found = sum(1 for v in fields.values() if v)
        print(f"  提取到 {found}/16 个字段")

        md_content = generate_markdown(name, code, product_code, fields)
        with open(md_path, 'w', encoding='utf-8') as f:
            f.write(md_content)
        print(f"  已保存: {md_path}")

        # Delete the HTML file after conversion
        os.remove(html_path)
        print(f"  已删除: {html_path}")


if __name__ == "__main__":
    main()
