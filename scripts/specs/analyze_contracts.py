"""
分析 76 份合约规格 .md 文件，输出结构化统计
"""
import sys
sys.stdout.reconfigure(encoding='utf-8')
import re
from pathlib import Path
from collections import Counter, defaultdict

DOCS_DIR = Path(r"C:\Works\ClaudeCode\TradingStudio\docs\contracts")

def parse_contract_md(filepath):
    """从合约 .md 文件中提取规格字段"""
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()

    spec = {}
    # Parse YAML frontmatter for exchange/category
    fm_match = re.search(r'-\s*"(\w+)"\s*$', content, re.MULTILINE)
    # Better: extract tags from frontmatter
    tags = re.findall(r'-\s*"([^"]+)"', content[:500])

    # Parse markdown table rows
    for line in content.split('\n'):
        m = re.match(r'\|\s*(.+?)\s*\|\s*(.+?)\s*\|', line)
        if m:
            key = m.group(1).strip()
            val = m.group(2).strip()
            # Skip header separators
            if key.startswith('-') or key == '期货':
                continue
            spec[key] = val

    # Determine exchange from path or tags
    rel = str(filepath.relative_to(DOCS_DIR))
    exchange = rel.split('/')[0] if '/' in rel else 'UNKNOWN'

    # Use tags to determine exchange if path-based fails
    if exchange == 'UNKNOWN':
        for t in tags:
            if t in ('SHFE', 'INE', 'DCE', 'CZCE', 'GFEX', 'CFFEX'):
                exchange = t
                break

    return spec, exchange, tags


def extract_numeric(val):
    """从规格字符串中提取数值和单位"""
    if not val or val == '—':
        return None, None, val

    # 交易单位: "5吨/手", "1000克/手", "每点300元", "面值100万元人民币"
    m = re.match(r'(\d+(?:\.\d+)?)\s*(吨|千克|克|桶|元|点|立方米|米|元/吨|指数点)?', val.replace(',', ''))
    if m:
        num = float(m.group(1))
        unit = m.group(2) if m.group(2) else ''
        return num, unit, val

    # Try to extract any number
    nums = re.findall(r'(\d+(?:\.\d+)?)', str(val))
    if nums:
        return float(nums[0]), '', val
    return None, None, val


def main():
    md_files = sorted(DOCS_DIR.rglob('*.md'))
    # Skip non-exchange directories (all_contracts, main_contracts, root md)
    contracts = []
    for f in md_files:
        if 'all_contracts' in str(f) or 'main_contracts' in str(f):
            continue
        contracts.append(f)

    print(f"总合约规格文件: {len(contracts)}")

    # Analyze
    exchanges = Counter()
    categories = Counter()
    fields_coverage = Counter()
    trading_units = []
    tick_sizes = []
    price_limits = []
    margin_rates = []
    month_patterns = Counter()
    delivery_types = Counter()
    missing_fields = defaultdict(list)

    all_specs = []

    for f in contracts:
        spec, exchange, tags = parse_contract_md(f)

        exchanges[exchange] += 1

        # Extract category from tags
        cat = '未知'
        for t in tags:
            if t in ('有色金属', '贵金属', '黑色金属', '黑色', '农产品', '化工',
                       '能源化工', '能源', '畜牧', '纺织', '建材', '林业',
                       '股指', '利率', '新能源', '指数'):
                cat = t
                break
        categories[cat] += 1

        # Field coverage
        for field in spec:
            if spec[field] and spec[field] != '—':
                fields_coverage[field] += 1
            else:
                missing_fields[field].append(f.name)

        # Trading unit
        if '交易单位' in spec:
            num, unit, raw = extract_numeric(spec['交易单位'])
            if num:
                trading_units.append((f.name, exchange, spec.get('交易代码', '?'), num, unit, raw))

        # Tick size
        if '最小变动价位' in spec:
            num, unit, raw = extract_numeric(spec['最小变动价位'])
            if num:
                tick_sizes.append((f.name, spec.get('交易代码', '?'), num, raw))

        # Price limit
        if '涨跌停板幅度' in spec:
            raw = spec['涨跌停板幅度']
            pct = re.findall(r'(\d+(?:\.\d+)?)%', raw)
            if '%' in raw:
                pass  # percentage-based
            price_limits.append((f.name, spec.get('交易代码', '?'), raw))

        # Margin
        if '最低交易保证金' in spec:
            raw = spec['最低交易保证金']
            pct = re.findall(r'(\d+(?:\.\d+)?)%', raw)
            if pct:
                margin_rates.append((f.name, spec.get('交易代码', '?'), float(pct[0]), raw))

        # Contract months
        if '合约月份' in spec:
            months = spec['合约月份']
            month_patterns[months] += 1

        # Delivery type
        if '交割方式' in spec:
            dt = spec['交割方式']
            if '现金' in dt:
                delivery_types['现金交割'] += 1
            elif '实物' in dt:
                delivery_types['实物交割'] += 1

        all_specs.append((f.name, exchange, spec))

    # === OUTPUT ===
    print(f"\n{'='*60}")
    print(f"一、交易所分布")
    print(f"{'='*60}")
    for ex, cnt in exchanges.most_common():
        print(f"  {ex}: {cnt} 品种")

    print(f"\n{'='*60}")
    print(f"二、品种分类")
    print(f"{'='*60}")
    for cat, cnt in categories.most_common():
        print(f"  {cat}: {cnt} 品种")

    print(f"\n{'='*60}")
    print(f"三、字段覆盖率")
    print(f"{'='*60}")
    for field, cnt in sorted(fields_coverage.items(), key=lambda x: -x[1]):
        total = len(contracts)
        pct = cnt / total * 100
        flag = " ✓" if pct == 100 else f" ⚠ {total-cnt} missing"
        print(f"  {field}: {cnt}/{total} ({pct:.0f}%){flag}")

    print(f"\n{'='*60}")
    print(f"四、交易单位多样性")
    print(f"{'='*60}")
    # Group by unit
    by_unit = defaultdict(list)
    for name, ex, code, num, unit, raw in trading_units:
        by_unit[unit if unit else '(无单位)'].append(f"{name}({code})")
    for unit, items in sorted(by_unit.items()):
        print(f"  {unit}: {len(items)} 品种 — {', '.join(items[:6])}{'...' if len(items)>6 else ''}")

    print(f"\n{'='*60}")
    print(f"五、最小变动价位多样性")
    print(f"{'='*60}")
    tick_sizes.sort(key=lambda x: x[2])
    seen = set()
    for name, code, num, raw in tick_sizes:
        if raw not in seen:
            seen.add(raw)
            matching = [t[0] for t in tick_sizes if t[3] == raw]
            print(f"  {raw}: {len(matching)} 品种 — {', '.join(matching[:5])}{'...' if len(matching)>5 else ''}")

    print(f"\n{'='*60}")
    print(f"六、保证金率分布")
    print(f"{'='*60}")
    margin_rates.sort(key=lambda x: x[2])
    seen = set()
    for name, code, rate, raw in margin_rates:
        if rate not in seen:
            seen.add(rate)
            matching = [m[0] for m in margin_rates if m[2] == rate]
            print(f"  {rate}% ({raw}): {len(matching)} 品种")

    print(f"\n{'='*60}")
    print(f"七、合约月份模式")
    print(f"{'='*60}")
    for pattern, cnt in month_patterns.most_common():
        print(f"  [{cnt}个] {pattern}")

    print(f"\n{'='*60}")
    print(f"八、交割方式")
    print(f"{'='*60}")
    for dt, cnt in delivery_types.most_common():
        print(f"  {dt}: {cnt} 品种")

    print(f"\n{'='*60}")
    print(f"九、缺失字段汇总")
    print(f"{'='*60}")
    for field, files in sorted(missing_fields.items()):
        if files:
            print(f"  {field}: {len(files)} 缺失 — {', '.join(files[:5])}{'...' if len(files)>5 else ''}")

    print(f"\n{'='*60}")
    print(f"十、各交易所完整品种列表")
    print(f"{'='*60}")
    for ex in sorted(exchanges.keys()):
        ex_items = [(n, s.get('交易代码', '?')) for n, e, s in all_specs if e == ex]
        print(f"\n## {ex} ({len(ex_items)} 品种)")
        for name, code in sorted(ex_items):
            spec = {n: s for n, e, s in all_specs if n == name}[0]
            unit = spec.get('交易单位', '?')
            tick = spec.get('最小变动价位', '?')
            margin = spec.get('最低交易保证金', '?')
            print(f"  {name:8s} | {code:4s} | {unit:20s} | {tick:15s} | {margin}")


if __name__ == '__main__':
    main()
