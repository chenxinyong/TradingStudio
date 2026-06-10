# 10 — 修正后的数据模型：基于 76 合约 + CTP 分析的最终设计

> 融合 docs/05（合约模型）、docs/08（合约分析）、docs/09（CTP 分析）
> 的结论，产出可实施的 PostgreSQL DDL。

---

## 一、实体层次

```
┌─────────────────────────────────────────┐
│  Exchange (交易所)                        │
│  └─ Symbol (品种: cu, rb, IF, sc...)     │
│       ├─ Contract (合约: cu2607...)       │
│       ├─ CommissionRule (手续费规则)       │
│       ├─ MarginRule (保证金规则)           │
│       └─ TradingSession (交易时段)         │
├─────────────────────────────────────────┤
│  TickRecord (80B, 内存/文件)              │
│  Bar (1min/1day, PostgreSQL)             │
└─────────────────────────────────────────┘
```

---

## 二、完整 PostgreSQL DDL

```sql
-- ============================================================
-- TradingStudio 数据模型 v2.0
-- 基于 76 份合约规格分析 + CTP v6.7.13 接口分析
-- ============================================================

-- ============================================================
-- 1. 交易所
-- ============================================================
CREATE TABLE exchanges (
    id          SMALLINT PRIMARY KEY,
    code        VARCHAR(10) NOT NULL UNIQUE,  -- 'SHFE','INE','DCE','CZCE','CFFEX','GFEX'
    name        VARCHAR(50) NOT NULL,         -- '上海期货交易所'
    name_short  VARCHAR(20) NOT NULL,         -- '上期所'
    country     VARCHAR(10) DEFAULT 'CN'
);

-- Seed data
INSERT INTO exchanges VALUES
(1, 'SHFE',  '上海期货交易所',     '上期所', 'CN'),
(2, 'INE',   '上海国际能源交易中心', '上能源', 'CN'),
(3, 'DCE',   '大连商品交易所',     '大商所', 'CN'),
(4, 'CZCE',  '郑州商品交易所',     '郑商所', 'CN'),
(5, 'CFFEX', '中国金融期货交易所',  '中金所', 'CN'),
(6, 'GFEX',  '广州期货交易所',     '广期所', 'CN');

-- ============================================================
-- 2. 品种（Symbol） — 永久性交易品种，如铜=cu
-- ============================================================
CREATE TABLE symbols (
    id                  SERIAL PRIMARY KEY,
    exchange_id         SMALLINT NOT NULL REFERENCES exchanges(id),
    code                VARCHAR(10) NOT NULL,          -- 'cu', 'rb', 'IF' (CFFEX大写)
    name                VARCHAR(50) NOT NULL,          -- '铜', '螺纹钢', '沪深300'
    product_category    VARCHAR(20),                   -- '有色金属'|'黑色金属'|'农产品'|'化工'|'能源'|'贵金属'|'股指'|'利率'|'新能源'
    trading_unit_value  NUMERIC(12,4) NOT NULL,       -- 5, 10, 1000, 0.02
    trading_unit_name   VARCHAR(20) NOT NULL,          -- '吨', '克', '千克', '桶', '立方米', '点', '元/点'
    quote_unit          VARCHAR(30) NOT NULL,          -- '元(人民币)/吨'
    tick_size           NUMERIC(12,6) NOT NULL,        -- 0.005 ~ 50
    price_limit_pct     NUMERIC(5,3) NOT NULL,         -- 0.03 = ±3%
    contract_months     VARCHAR(100) NOT NULL,          -- '1～12月' | '1,3,5,7,9,11'
    trading_hours       VARCHAR(200) NOT NULL,
    last_trading_day    VARCHAR(100) NOT NULL,          -- 规则描述
    delivery_days       VARCHAR(100) NOT NULL,
    delivery_type       VARCHAR(10) NOT NULL            -- 'PHYSICAL' | 'CASH'
        CHECK (delivery_type IN ('PHYSICAL', 'CASH')),
    delivery_unit_value NUMERIC(12,4),                 -- 交割单位数值 (NULL if same as trading)
    delivery_unit_name  VARCHAR(20),                   -- 交割单位名称 (NULL if cash-settled)
    margin_rate         NUMERIC(5,3) NOT NULL,          -- 交易所基准保证金率（如 0.05 = 5%）
    delivery_grade      TEXT,
    delivery_location   VARCHAR(200),
    margin_description  VARCHAR(100),

    UNIQUE (exchange_id, code)
);

CREATE INDEX idx_symbols_exchange ON symbols(exchange_id);
CREATE INDEX idx_symbols_category ON symbols(product_category);

-- ============================================================
-- 3. 合约（Contract） — 具体的到期月份合约，如 cu2607
-- ============================================================
CREATE TABLE contracts (
    id              SERIAL PRIMARY KEY,
    symbol_id       INTEGER NOT NULL REFERENCES symbols(id),
    code            VARCHAR(20) NOT NULL,          -- 'cu2607', 'IF2606'
    listing_date    DATE,                          -- 上市日
    expiry_date     DATE NOT NULL,                 -- 最后交易日
    delivery_start  DATE,                          -- 开始交割日
    delivery_end    DATE,                          -- 最后交割日
    is_active       BOOLEAN DEFAULT TRUE,
    created_at      TIMESTAMPTZ DEFAULT NOW(),

    UNIQUE (symbol_id, code)
);

CREATE INDEX idx_contracts_symbol ON contracts(symbol_id);
CREATE INDEX idx_contracts_expiry ON contracts(expiry_date);
CREATE INDEX idx_contracts_active ON contracts(is_active) WHERE is_active;

-- ============================================================
-- 4. 手续费规则（CommissionRule） — 按生效日期版本化
-- ============================================================
CREATE TYPE fee_mode AS ENUM ('BOTH', 'TODAY_FREE', 'TODAY_PENALTY', 'ONE_SIDE');

CREATE TABLE commission_rules (
    id                  SERIAL PRIMARY KEY,
    symbol_id           INTEGER NOT NULL REFERENCES symbols(id),
    effective_date      DATE NOT NULL,             -- 生效日期
    fee_mode            fee_mode NOT NULL,
    open_fee_pct        NUMERIC(10,8) DEFAULT 0,   -- 开仓 万分之X
    open_fee_fixed       NUMERIC(12,4) DEFAULT 0,   -- 开仓 固定X元/手
    close_yday_fee_pct  NUMERIC(10,8) DEFAULT 0,   -- 平昨 万分之X
    close_yday_fee_fixed NUMERIC(12,4) DEFAULT 0,
    close_today_fee_pct NUMERIC(10,8) DEFAULT 0,   -- 平今 万分之X
    close_today_fee_fixed NUMERIC(12,4) DEFAULT 0,
    updated_at          TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_commission_symbol ON commission_rules(symbol_id);
CREATE INDEX idx_commission_effective ON commission_rules(symbol_id, effective_date DESC);

-- ============================================================
-- 5. 保证金规则（MarginRule） — 按生效日期版本化
-- ============================================================
CREATE TABLE margin_rules (
    id              SERIAL PRIMARY KEY,
    symbol_id       INTEGER NOT NULL REFERENCES symbols(id),
    effective_date  DATE NOT NULL,
    exchange_rate   NUMERIC(5,3) NOT NULL,         -- 交易所基准保证金率
    broker_rate     NUMERIC(5,3),                  -- 期货公司加收后（NULL=未配置）
    description     VARCHAR(200),                  -- '节假日调整', '临近交割月调整'
    updated_at      TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_margin_symbol ON margin_rules(symbol_id);
CREATE INDEX idx_margin_effective ON margin_rules(symbol_id, effective_date DESC);

-- ============================================================
-- 6. 交易时段（TradingSession）
-- ============================================================
CREATE TABLE trading_sessions (
    id              SERIAL PRIMARY KEY,
    symbol_id       INTEGER NOT NULL REFERENCES symbols(id),
    session_type    VARCHAR(10) NOT NULL            -- 'DAY' | 'NIGHT'
        CHECK (session_type IN ('DAY', 'NIGHT')),
    start_time      TIME NOT NULL,                  -- 09:00:00
    end_time        TIME NOT NULL,                  -- 10:15:00
    is_active       BOOLEAN DEFAULT TRUE
);

CREATE INDEX idx_session_symbol ON trading_sessions(symbol_id);

-- ============================================================
-- 7. Bar 数据（1分钟 / 日线）
-- ============================================================
CREATE TABLE bars_1min (
    id              BIGSERIAL PRIMARY KEY,
    contract_id     INTEGER NOT NULL REFERENCES contracts(id),
    trading_day     DATE NOT NULL,                 -- 交易日（夜盘归属日）
    bar_time        TIMESTAMPTZ NOT NULL,           -- K线开始时间
    open            BIGINT NOT NULL,                -- 开 × 10^7
    high            BIGINT NOT NULL,
    low             BIGINT NOT NULL,
    close           BIGINT NOT NULL,
    volume          BIGINT NOT NULL,                -- 成交量（delta from ticks）
    turnover        DOUBLE PRECISION NOT NULL,      -- 成交额（delta）
    open_interest   DOUBLE PRECISION NOT NULL,
    tick_count      INTEGER DEFAULT 0,             -- 构成此Bar的tick数

    UNIQUE (contract_id, bar_time)
);

CREATE INDEX idx_bars_1min_contract ON bars_1min(contract_id, bar_time);
CREATE INDEX idx_bars_1min_day ON bars_1min(trading_day);

-- 日线表（从 1min 合成，但预计算存一份加速查询）
CREATE TABLE bars_day (
    id              BIGSERIAL PRIMARY KEY,
    contract_id     INTEGER NOT NULL REFERENCES contracts(id),
    trading_day     DATE NOT NULL,
    open            BIGINT NOT NULL,
    high            BIGINT NOT NULL,
    low             BIGINT NOT NULL,
    close           BIGINT NOT NULL,
    volume          BIGINT NOT NULL,
    turnover        DOUBLE PRECISION NOT NULL,
    open_interest   DOUBLE PRECISION NOT NULL,

    UNIQUE (contract_id, trading_day)
);

CREATE INDEX idx_bars_day_contract ON bars_day(contract_id, trading_day);
```

---

## 三、设计决策记录

### 3.1 trading_unit 为什么拆成两列

`trading_unit_value` + `trading_unit_name` 而不是一个 `VARCHAR(50)`：

- 62 个品种用"吨"，4 个 CFFEX 股指用"每点×元"，2 个国债用"面值×万元"
- 需要数值来做手数→吨数的换算（1 手铜 = 5 吨），不能只存字符串

### 3.2 delivery_type 为什么是枚举

71 个实物交割 vs 5 个现金交割——这是期货品种最根本的差异：
- 现金交割品种没有交割品级/地点/单位三个字段
- 回测中现金交割品种不存在"进入交割月"的特殊处理

### 3.3 product_category 的用途

- 策略研发："我想在有色金属上跑一个配对交易" → `WHERE product_category = '有色金属'`
- 风险监控："黑色系全线跌停" → 按 category 聚合
- 数据验证："农产品不应该有夜盘到 2:30" → category 级别的规则检查

### 3.4 TickRecord 保持 80 字节

InstrumentID 和 TradingDay **不在每条 tick 记录中存储**：
- `.tick` 文件路径 `{tradingDay}/{instrumentId}.tick` 已经编码了这两个信息
- 80 字节 = 一个 cache line 大小，CPU 友好
- 如果要加字段，Header 扩展比 Record 扩展更灵活

### 3.5 价格精度：BIGINT × 10^7

所有价格存为 `BIGINT`（内部 ×10^7），不是 `NUMERIC`：
- 期货价格范围：SCFIS欧线 ~6000 点，国债 ~100 元——10^7 精度足够
- 整数运算比 decimal 快 10-50 倍
- `.tick` 二进制文件可以 memcpy 整条记录

---

## 四、Seed Data 策略

```python
# gen_final_specs.py 已能从 AKShare 拉取实时的：
#   1. 手续费（九期网 futures_comm_info）
#   2. 合约详情（东财 futures_contract_detail_em）
#   3. 六交易所完整品种列表
#
# 输出：六大交易所合约规格表.md → Obsidian 知识库
# 新增：生成 PostgreSQL INSERT 语句 → docs/contracts/seed_data.sql
```

---

## 五、与旧文档的关系

| 旧文档 | 本修订 |
|---|---|
| doc 05 DDL | 融合：新增 4 个字段，修正 code 大小写，trading_unit 拆分 |
| doc 02 TickRecord | 确认：80 字节不变，InstrumentID/TradingDay 走文件路径 |
| doc 03 CTP wrapper | doc 09 替代：bridge 回调需要加 instrumentId/tradingDay 参数 |
| doc 06 架构 | 不变：接口定义和管线设计仍然有效 |

---

*设计日期：2026-06-10*
*数据基础：docs/contracts/ 76 份合约规格 + CTP SDK 6.7.13*
