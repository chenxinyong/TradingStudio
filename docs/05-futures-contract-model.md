# 期货合约数据模型设计

> 从第一性原理出发，理解期货合约的本质 + CTP 接口的结构，推导数据模型。

---

## 1. 期货的本质

### 1.1 什么是期货合约？

```
期货合约 = 标准化的远期交易协议

标准化意味着：
  - 交易单位（数量）是固定的
  - 交割月份是预设的
  - 最小价格变动是规定的
  - 交易时间是固定的
  - 所有条款由交易所定义，交易者只选择"方向"和"手数"

例如 SHFE 铜合约：
  - 每手 = 5 吨（固定）
  - 每年 12 个月份各有合约（cu2606, cu2607, ..., cu2705）
  - 最小变动 = 10 元/吨 → 每跳价值 = 5 × 10 = 50 元
  - 保证金 ≈ 5% → 104,000 × 5 × 5% ≈ 26,000 元/手
```

### 1.2 三层实体层级

```
┌─────────────────────────────────────────────────────┐
│ Exchange（交易所）                                    │
│   定义了交易规则的大框架                                │
│   └─ SHFE, DCE, ZCE, CFFEX, INE, GFEX               │
├─────────────────────────────────────────────────────┤
│ Symbol（品种）                                        │
│   可交易的标的类型。永续存在，不随到期消失。              │
│   └─ cu（铜）, ag（白银）, IF（沪深300股指）            │
├─────────────────────────────────────────────────────┤
│ Contract（合约）                                      │
│   具体到期月份的合约。有生命周期：上市 → 交易 → 到期。   │
│   └─ cu2607（铜 2026年7月合约）                        │
└─────────────────────────────────────────────────────┘
```

**关键推导**：

```
→ Symbol 的属性是所有同品种合约共享的（交易单位、tick 大小、手续费规则）
→ Contract 的属性是每个合约独有的（交割月份、上市日、到期日）
→ 手续费和保证金按 Symbol 定义，但按生效日期分版本（支持历史追溯）
→ Tick/Bar 属于 Contract（行情总是针对具体合约）
→ Order/Fill 属于 Contract（交易总是针对具体合约）
```

---

## 2. 从 SHFE 合约规格文档提取字段

以铜合约规格为例，从交易所文档映射到数据模型：

### 2.1 Symbol 层（品种规格）

```
SHFE 铜合约规格                   → Symbol 字段
══════════════════════════════    ═══════════════════════════════
交易品种: 阴极铜                   → NameCn = "阴极铜"
交易单位: 5吨/手                   → TradingUnit = 5 (decimal)
报价单位: 元（人民币）/吨           → PriceUnit = "元/吨"
最小变动价位: 10元/吨              → TickSize = 10 (decimal)
                                  → TickValue = 5 × 10 = 50 (derived)
涨跌停板幅度: ±3%                  → PriceLimitFraction = 0.03 (decimal)
合约月份: 1～12月                  → ContractMonths = "1-12"
交易时间: 上午9:00－11:30，        → TradingSessions (FK 到 sessions 表)
         下午1:30－3:00和夜盘
最后交易日: 合约月份的15日          → LastTradingDayRule = "15日"
最低交易保证金: 合约价值的5%        → MarginRateBase = 0.05 (decimal)
交易代码: CU                      → Code = "cu"
上市交易所: 上海期货交易所           → ExchangeId (FK 到 exchanges 表)
```

### 2.2 Symbol 字段汇总

```csharp
public class Symbol
{
    // ---- 标识 ----
    public int Id { get; set; }
    public int ExchangeId { get; set; }           // FK → Exchange
    public string Code { get; set; }              // "cu" (小写，用于 CTP 订阅)
    public string NameCn { get; set; }            // "阴极铜"

    // ---- 合约规格（固定不变） ----
    public decimal TradingUnit { get; set; }      // 5 (吨/手)
    public decimal TickSize { get; set; }         // 10 (元/吨)
    public decimal TickValue { get; set; }        // 50 = 5 × 10 (元/跳)
    public string PriceUnit { get; set; }         // "元/吨"
    public decimal PriceLimitFraction { get; set; } // 0.03 (3%)
    public string ContractMonths { get; set; }    // "1-12"
    public string LastTradingDayRule { get; set; } // "15日"
    public decimal MarginRateBase { get; set; }   // 0.05
    public decimal? MarginRateActual { get; set; } // 期货公司加收后的实际保证金率

    // ---- 衍生 ----
    public bool IsActive { get; set; }
    public Exchange Exchange { get; set; }
    public ICollection<Contract> Contracts { get; set; }
    public ICollection<CommissionRule> CommissionRules { get; set; }
    public ICollection<MarginRule> MarginRules { get; set; }
}
```

### 2.3 三个品种的规格对比（验证模型通用性）

| 字段 | cu (铜) | bc (国际铜) | ag (白银) | 模型是否覆盖 |
|---|---|---|---|---|
| 交易单位 | 5吨/手 | 5吨/手 | **15千克/手** | TradingUnit ✅ |
| 最小变动 | 10元/吨 | 10元/吨 | **1元/千克** | TickSize ✅ |
| 涨跌停 | ±3% | ±3% | ±3% | PriceLimitFraction ✅ |
| 保证金 | 5% | 5% | **4%** | MarginRateBase ✅ |
| 报价单位 | 元/吨 | 元/吨(不含税) | 元/千克 | PriceUnit ✅ |
| 合约月份 | 1-12月 | 1-12月 | 1-12月 | ContractMonths ✅ |
| 最后交易日 | 15日 | 15日 | 15日 | LastTradingDayRule ✅ |
| 交易代码 | **CU** | **BC** | **AG** | Code ✅ |

**结论**：三个品种的差异仅在数值，不在结构。模型通用。

---

## 3. CTP 接口视角的数据模型

### 3.1 CTP 行情字段 → Tick

```
CTP MdApi 推送的每个 tick 包含两类信息：

A. tick 自带的（来自交易所，每条不同）：
  InstrumentID, TradingDay, UpdateTime, UpdateMillisec
  LastPrice, Volume, Turnover, OpenInterest
  BidPrice1-5, BidVolume1-5
  AskPrice1-5, AskVolume1-5
  UpperLimitPrice, LowerLimitPrice
  OpenPrice, HighestPrice, LowestPrice
  PreSettlementPrice, PreClosePrice, PreOpenInterest

  → 这些映射到 TickRecord（见 02-data-model-spec.md）

B. 合约固有的（同一合约所有 tick 共享，不应在每条 tick 里重复存）：
  交易单位、TickSize、涨跌停板幅度
  手续费率、保证金率
  交易时段

  → 这些从 Symbol 模型获取（tick 只存 Symbol/Contract 引用，不存规格）
```

### 3.2 Tick 只存什么

```
TickRecord 的设计原则：
  
  只存"这条 tick 特有的、不能从其他地方推导"的字段。
  Symbol 的规格信息（tick 大小、合约乘数、交易时段）
  在需要时通过 Symbol/Contract 引用查询——不在每条 tick 里冗余存储。

TickRecord (80 bytes):
  ExchangeTimestamp    ← UpdateTime + Millisec
  LocalTimestamp       ← 本机时间
  LastPrice            ← LastPrice × 10⁷
  Volume               ← 累计成交量（增量由 Bar 聚合计算）
  Turnover             ← 累计成交额
  OpenInterest         ← 持仓量
  BidPrice1, BidVol1   ← 买一
  AskPrice1, AskVol1   ← 卖一
  Flags                ← 涨停/跌停/集合竞价

不在 TickRecord 中的：
  InstrumentID         → 文件名隐含
  TradingDay           → 文件名隐含
  Upper/LowerLimitPrice → 从 Symbol + PreSettlement 推导（flags 里标记是否触及）
  深度 L2-L5           → 丢弃（中低频不需要）
```

### 3.3 CTP 交易接口 → Order / Fill / Position

```
CTP TraderApi 推送的关键结构体：

  CThostFtdcOrderField       → OrderRecord
    订单状态变化通知。下单成功/部分成交/全部成交/撤单都会触发。
    
  CThostFtdcTradeField       → FillRecord  
    成交回报。每笔成交触发一次。
    
  CThostFtdcInvestorPositionField → PositionRecord
    持仓查询的返回结果。区分多头/空头、今仓/昨仓。

关键：CTP 的 Order 和 Trade 是分离的。
  一个 Order 可能对应多个 Trade（部分成交）
  Order.Status 由 CTP 维护，系统不应自行修改
```

---

## 4. 手续费规则模型（核心复杂性）

### 4.1 第一性原理推导

```
手续费模型要回答的问题：
  "我在 2024-06-15 开仓 5 手 rb2410，2024-06-15 平仓，手续费是多少？"

回答这个问题需要：
  1. rb 的手续费规则（在 2024-06-15 那天生效的是哪个版本？）
  2. 开仓费率 vs 平今费率 vs 平昨费率
  3. 按成交金额算还是按手数算？
  4. 交易时的成交价格
```

### 4.2 模型设计

```csharp
public enum FeeMode
{
    单边 = 1,     // 只收开仓
    双边 = 2,     // 开平都收
    平今免 = 3,   // 平今仓免费 (SHFE rb, INE nr, ...)
    平今加倍 = 4  // 平今仓加倍 (CFFEX IF/IC/IM/IH, SHFE cu, ...)
}

public class CommissionRule
{
    public int Id { get; set; }
    public int SymbolId { get; set; }

    public DateOnly EffectiveDate { get; set; }  // 生效日期（支持历史查询）
    public FeeMode FeeMode { get; set; }

    // 开仓
    public decimal? OpenFeeByPct { get; set; }   // 按金额万分比 (0.0001 = 万1)
    public decimal? OpenFeeFixed { get; set; }    // 按手固定 (元/手)

    // 平昨
    public decimal? CloseYesterdayFeeByPct { get; set; }
    public decimal? CloseYesterdayFeeFixed { get; set; }

    // 平今
    public decimal? CloseTodayFeeByPct { get; set; }
    public decimal? CloseTodayFeeFixed { get; set; }
}
```

**验证：能否覆盖三个 SHFE 品种的所有手续费场景？**

```
cu (铜)：双边，平今不加倍
  FeeMode = 双边
  OpenFeeByPct = 0.00005, CloseYesterdayFeeByPct = 0.00005, CloseTodayFeeByPct = 0.00005
  ✅

ag (白银)：双边
  FeeMode = 双边
  OpenFeeByPct = 0.00005, CloseYesterdayFeeByPct = 0.00005, CloseTodayFeeByPct = 0.00005
  ✅

rb (螺纹钢)：双边，平今免 — 模型是否支持？
  FeeMode = 平今免
  OpenFeeByPct = 0.0001, CloseYesterdayFeeByPct = 0.0001, CloseTodayFeeByPct = null
  → CloseTodayFeeByPct = null 表示平今免费 ✅

IF (沪深300)：平今 10 倍 — 模型是否支持？
  FeeMode = 平今加倍
  OpenFeeByPct = 0.000023 (万0.23)
  CloseYesterdayFeeByPct = 0.000023
  CloseTodayFeeByPct = 0.00023 (万2.3，10倍)
  ✅

c (玉米)：按手固定
  FeeMode = 双边
  OpenFeeFixed = 1.2 (1.2元/手)
  CloseYesterdayFeeFixed = 1.2, CloseTodayFeeFixed = 1.2
  ✅
```

---

## 5. 保证金规则模型

### 5.1 模型设计

```csharp
public class MarginRule
{
    public int Id { get; set; }
    public int SymbolId { get; set; }

    public DateOnly EffectiveDate { get; set; }  // 生效日期
    public decimal OpenBuyRate { get; set; }     // 买开保证金率 (%)
    public decimal OpenSellRate { get; set; }    // 卖开保证金率 (%)
    public decimal? MarginPerLot { get; set; }   // 每手固定保证金（用于参考）
}
```

### 5.2 验证：长假保证金调整

```
场景：2026-09-28 (国庆节前)，cu 保证金从 5% → 8%

MarginRule 表：
  { SymbolId=cu, EffectiveDate=2026-06-01, OpenBuyRate=5%, OpenSellRate=5% }
  { SymbolId=cu, EffectiveDate=2026-09-28, OpenBuyRate=8%, OpenSellRate=8% }
  { SymbolId=cu, EffectiveDate=2026-10-10, OpenBuyRate=5%, OpenSellRate=5% }

查询 2026-09-29 的保证金：
  SELECT * FROM margin_rules
  WHERE symbol_id=@cu AND effective_date <= '2026-09-29'
  ORDER BY effective_date DESC LIMIT 1
  → 返回 8% ✅
```

---

## 6. 交易时段模型

### 6.1 模型设计

```csharp
public class TradingSession
{
    public int Id { get; set; }
    public ExchangeCode ExchangeCode { get; set; }
    public string SymbolCode { get; set; }          // null = 交易所级别通用

    public string SessionName { get; set; }         // "上午第一节", "夜盘"
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int SortOrder { get; set; }
    public bool IsCrossDay { get; set; }            // 跨自然日 (夜盘 21:00-02:30)
}
```

### 6.2 验证：覆盖不同夜盘收盘时间

```
SHFE cu 交易时段：
  1. 上午第一节 09:00-10:15
  2. 上午第二节 10:30-11:30
  3. 下午      13:30-15:00
  4. 夜盘      21:00-01:00  ← IsCrossDay=true（次日凌晨）

DCE m 交易时段：
  1. 上午第一节 09:00-10:15
  2. 上午第二节 10:30-11:30
  3. 下午      13:30-15:00
  4. 夜盘      21:00-23:00  ← 比 cu 早 2 小时收盘

CFFEX IF 交易时段：
  1. 上午 09:30-11:30       ← 比商品期货晚 30 分钟开盘
  2. 下午 13:00-15:00       ← 比商品期货早 30 分钟开盘
  3. 无夜盘

→ TradingSession 按 SymbolCode 配置，三个品种各自独立 ✅
```

---

## 7. 合约（Contract）模型

```csharp
public class Contract
{
    public int Id { get; set; }
    public int SymbolId { get; set; }

    public string ContractCode { get; set; }     // "cu2607", "ag2612"
    public DateOnly DeliveryMonth { get; set; }  // 2026-07-01
    public DateOnly? ListingDate { get; set; }   // 上市日
    public DateOnly? LastTradeDate { get; set; } // 最后交易日
    public DateOnly? LastDeliveryDate { get; set; } // 最后交割日

    public bool IsMainContract { get; set; }     // 是否当前主力合约

    public Symbol Symbol { get; set; }
}
```

**合同代码推导规则**：

```
期货合约代码 = 品种代码（小写 1-2 字母）+ 到期年月（4 位数字）

  cu + 2607 = cu2607    铜 2026年7月合约
  ag + 2612 = ag2612    白银 2026年12月合约
  IF + 2606 = IF2606    沪深300股指 2026年6月合约  ← 股指用大写

CTP InstrumentID = 合约代码（与 ContractCode 一致）
```

---

## 8. 实体关系总图

```
┌──────────────┐
│   Exchange   │
│   Id, Code   │
└──────┬───────┘
       │ 1:N
       ▼
┌──────────────┐       ┌─────────────────┐
│    Symbol    │──────▶│ CommissionRule   │
│   Id, Code   │  1:N  │ EffectiveDate,   │
│   TradingUnit│       │ FeeMode, Rates   │
│   TickSize   │       └─────────────────┘
│   TickValue  │
│   MarginRate │       ┌─────────────────┐
└──────┬───────┘──────▶│  MarginRule      │
       │          1:N  │ EffectiveDate,   │
       │               │ Rates            │
       │ 1:N           └─────────────────┘
       ▼
┌──────────────┐       ┌─────────────────┐
│   Contract   │──────▶│     Tick        │
│  ContractCode│  1:N  │ (80 bytes, .tick)│
│  DeliveryMonth│      └─────────────────┘
│  IsMain      │
└──────────────┘       ┌─────────────────┐
                       │     Bar          │
                       │ (PostgreSQL)     │
                       └─────────────────┘

┌──────────────┐       ┌─────────────────┐
│TradingSession│       │TradingCalendar   │
│ ExchangeCode │       │ Date, IsTrading  │
│ SymbolCode   │       └─────────────────┘
│ Start/End    │
└──────────────┘
```

---

## 9. PostgreSQL 建表

```sql
-- 9.1 交易所
CREATE TABLE exchanges (
    id      SMALLINT PRIMARY KEY,
    code    VARCHAR(10) NOT NULL UNIQUE,
    name_cn VARCHAR(50) NOT NULL
);

-- 9.2 品种
CREATE TABLE symbols (
    id              SERIAL PRIMARY KEY,
    exchange_id     SMALLINT NOT NULL REFERENCES exchanges(id),
    code            VARCHAR(10) NOT NULL,
    name_cn         VARCHAR(50) NOT NULL,
    trading_unit    NUMERIC(12,4) NOT NULL,
    tick_size       NUMERIC(12,6) NOT NULL,
    tick_value      NUMERIC(12,2) NOT NULL,    -- 冗余但加速计算
    price_unit      VARCHAR(20),
    price_limit_pct NUMERIC(5,4) NOT NULL,     -- 0.0300 = 3%
    margin_rate_base NUMERIC(5,2) NOT NULL,
    margin_rate_actual NUMERIC(5,2),
    contract_months VARCHAR(50),
    last_trade_day_rule VARCHAR(50),
    is_active       BOOLEAN DEFAULT TRUE,
    UNIQUE(exchange_id, code)
);

-- 9.3 合约
CREATE TABLE contracts (
    id              SERIAL PRIMARY KEY,
    symbol_id       INT NOT NULL REFERENCES symbols(id),
    contract_code   VARCHAR(20) NOT NULL UNIQUE,
    delivery_month  DATE NOT NULL,
    listing_date    DATE,
    last_trade_date DATE,
    last_delivery_date DATE,
    is_main_contract BOOLEAN DEFAULT FALSE
);
CREATE INDEX idx_contracts_symbol ON contracts(symbol_id);

-- 9.4 手续费规则
CREATE TABLE commission_rules (
    id                      SERIAL PRIMARY KEY,
    symbol_id               INT NOT NULL REFERENCES symbols(id),
    effective_date          DATE NOT NULL,
    fee_mode                SMALLINT NOT NULL,
    open_fee_by_pct         NUMERIC(10,8),
    open_fee_fixed          NUMERIC(10,4),
    close_yesterday_fee_by_pct NUMERIC(10,8),
    close_yesterday_fee_fixed  NUMERIC(10,4),
    close_today_fee_by_pct  NUMERIC(10,8),
    close_today_fee_fixed   NUMERIC(10,4),
    UNIQUE(symbol_id, effective_date)
);

-- 9.5 保证金规则
CREATE TABLE margin_rules (
    id              SERIAL PRIMARY KEY,
    symbol_id       INT NOT NULL REFERENCES symbols(id),
    effective_date  DATE NOT NULL,
    open_buy_rate   NUMERIC(5,2) NOT NULL,
    open_sell_rate  NUMERIC(5,2) NOT NULL,
    margin_per_lot  NUMERIC(12,2),
    UNIQUE(symbol_id, effective_date)
);

-- 9.6 交易日历
CREATE TABLE trading_calendar (
    date            DATE PRIMARY KEY,
    is_trading_day  BOOLEAN NOT NULL DEFAULT TRUE,
    exchange_code   SMALLINT,
    notes           VARCHAR(100)
);

-- 9.7 交易时段
CREATE TABLE trading_sessions (
    id              SERIAL PRIMARY KEY,
    exchange_code   SMALLINT NOT NULL,
    symbol_code     VARCHAR(10),
    session_name    VARCHAR(20) NOT NULL,
    start_time      TIME NOT NULL,
    end_time        TIME NOT NULL,
    sort_order      SMALLINT DEFAULT 0,
    is_cross_day    BOOLEAN DEFAULT FALSE
);
```

---

## 10. 验证：用 SHFE 合约数据完整走通

### 10.1 插入铜品种数据

```sql
INSERT INTO exchanges VALUES (1, 'SHFE', '上海期货交易所');

INSERT INTO symbols (exchange_id, code, name_cn, trading_unit, tick_size, tick_value,
    price_unit, price_limit_pct, margin_rate_base, contract_months, last_trade_day_rule)
VALUES (1, 'cu', '阴极铜', 5, 10, 50, '元/吨', 0.03, 5, '1-12', '15日');
```

### 10.2 插入铜合约数据

```sql
-- 从 SHFE 官网获取的 12 个合约
INSERT INTO contracts (symbol_id, contract_code, delivery_month, listing_date, last_trade_date)
VALUES
  (1, 'cu2606', '2026-06-01', '2025-06-17', '2026-06-15'),
  (1, 'cu2607', '2026-07-01', '2025-07-16', '2026-07-15'),
  (1, 'cu2608', '2026-08-01', '2025-08-18', '2026-08-17'),
  ...
  (1, 'cu2705', '2027-05-01', '2026-05-18', '2027-05-17');
```

### 10.3 查询路径

```
"我需要 cu2607 的 TickSize"
  → SELECT tick_size FROM symbols WHERE code='cu'
  → 10 元/吨 ✅

"我收到 cu2607 的 tick，LastPrice=104650，这是多少元/手？"
  → 104650 元/吨 × 5 吨/手 = 523,250 元/手

"cu2607 今天涨停价是多少？"
  → PreSettlement = 104410（来自上一个 tick）
  → 涨停 = 104410 × (1 + 0.03) = 107,542（上取整到 tick 倍数 = 107,550）
  → 或直接取 tick 中的 UpperLimitPrice 字段

"2024-06-15 开仓 5 手 cu2407 手续费多少？"
  → 查 CommissionRule WHERE effective_date <= '2024-06-15' ORDER BY effective_date DESC
  → FeeMode = 双边, OpenFeeByPct = 0.00005
  → 手续费 = 成交价 × 5 吨 × 5 手 × 0.00005
```

---

## 11. 结论

数据模型的根基是三层实体：**Exchange → Symbol → Contract**

```
Symbol 定义品种规格（不变属性 + 可变规则按日期版本化）
Contract 定义具体合约（有生命周期的交易对象）
CommissionRule/MarginRule 按生效日期历史追溯（回测基础）
TradingSession/TradingCalendar 驱动系统行为（时段切换、交易日判断）
```

所有上层功能（行情采集、回测、风控、执行）都建立在这个模型之上。
