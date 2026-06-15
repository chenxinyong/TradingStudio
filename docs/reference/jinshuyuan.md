# 金数源 — 历史Tick数据源

> www.jinshuyuan.net — 国内期货Tick数据供应商

---

## 1. 账号信息

| 项目 | 值 |
|------|-----|
| 邮箱 | `chenxinyongcz@qq.com` |
| RAR 密码 | `www.jinshuyuan.net` |
| API Key | `157145f8520657fefcc45fe897153118` |

## 2. 订单状态 (2026-06-15)

| 类型 | 数量 |
|------|:---:|
| 未支付 | 0 |
| 服务中 | 0 |
| 已完结 | 0 |
| 其他 | 0 |

⚠️ 当前无有效订阅。历史RAR文件为离线下载或试用期获取，在线API需购买订阅后使用。

## 3. 数据产品

### FutAC_TickKZ_CTP_Daily — 期货Tick快照（CTP格式，日级）

- 格式: CTP 44列，GBK编码
- 覆盖: 六大交易所（SHFE/DCE/CZCE/INE/GFEX/CFFEX）
- 包含: 主力连续 + 次主力连续 + 全部月份合约
- 周期: 2015年至今

### 本地存储

```
C:\Works\Datas\Jinshuyuan\
├── FutAC_TickKZ_CTP_Daily_2020\
│   ├── FutAC_TickKZ_CTP_Daily_202001.rar
│   ├── FutAC_TickKZ_CTP_Daily_202002.rar
│   └── ...
├── FutAC_TickKZ_CTP_Daily_2021\
└── ...
```

RAR内部结构: `{exchange}/{contract}_{YYYYMMDD}.csv`

交易所代码: `dc`=DCE, `sc`=SHFE, `zc`=ZCE, `ine`=INE, `gfex`=GFEX

## 4. API 接口

### 获取今日文件URL

```
GET http://api.jinshuyuan.net/get_today_fileurl?apikey={key}&pdtnm={product_id}

参数:
  apikey  - API Key
  pdtnm   - 产品编号 (202 = 期货Tick快照CTP日级)
```

## 5. 导入管线

### 工具

```
TradingStudio.ToolBox import-jinshuyuan [选项]

选项:
  --layer / -l        main|active|all
  --from-month / -f   YYYYMM
  --to-month / -t     YYYYMM
  --db                SQLite路径
  --data-dir          金数源RAR目录
  --symbol / -s       品种过滤
```

### 示例

```bash
# 导入全部年份
ToolBox ij -l active -f 202001 -t 202512 --db bars_history.db

# 单品种
ToolBox ij -l active -f 202001 -t 202512 -s rb --db bars_history.db
```

### 流程

```
RAR → UnRAR 解压到临时目录 → CsvTickImporter 流式解析
    → BarAggregator (1min) + DailyBarAggregator (Day)
    → BarStore.WriteBatchAsync → bars_history.db (SQLite)
    → 删除临时CSV
```

- 功率性: `INSERT OR REPLACE` + `UNIQUE(instrument_id, bar_time)`
- 支持中断续传
