# 13 — UI 技术选型

> TradingStudio 前端技术栈评估与最终决策。
>
> **版本**: v1.0 | **日期**: 2026-06-15 | **状态**: 已确定

---

## 决策

| 层次 | 选择 | 授权 | 阶段 |
|------|------|------|------|
| 桌面框架 | **WPF** (.NET 10, x64) | — | 全程 |
| MVVM 工具 | **CommunityToolkit.Mvvm** 8.x | MIT | 全程 |
| K 线图主力 | **OxyPlot** 2.2 | MIT | Phase 2-3 |
| 图表升级 A | ProEssentials v10 | 买断 ~$500 | Phase 4 |
| 图表升级 B | SciChart WPF | 订阅 ~$1,095/年 | Phase 4 |
| UI 组件库 | WPF 原生控件 | — | Phase 2-3 |
| UI 组件库升级 A | HandyControl 3.5 | MIT | Phase 4 |
| UI 组件库升级 B | Syncfusion 社区版 | 社区免费 | Phase 4 |

## Why WPF

- **C# 生态一致性**: 引擎 (.NET 10) 和 UI 同语言，可共享 `TradingStudio.Core` 类型（Bar、TickRecord、Order、StrategySnapshot）——无需序列化适配层
- **20 年 C# 经验**: 术业有专攻，不做无意义的"学习新技术"
- **MVVM 原生支持**: WPF 数据绑定是最成熟的 MVVM 实现，CommunityToolkit.Mvvm 源码生成器进一步简化
- **部署简单**: 单 exe 发布 (`dotnet publish`)，无需 Web 服务器、浏览器兼容

## Why OxyPlot

- **MVVM 原生**: `PlotModel` 是纯数据对象，ViewModel 可单元测试。不需要 `Behavior` 桥接或 UI 控件引用
- **K 线图支持**: `CandleStickSeries` + `RectangleBarSeries` + `LineSeries` + `Annotation` 覆盖全部需求
- **性能**: 10 万数据点内渲染 < 100ms，满足期货日内 K 线需求
- **免费**: MIT 协议

**ScottPlot 5 的排除**: 官方 FAQ 明确声明不支持 MVVM/数据绑定，建议"Write your own user control"——打破 MVVM 硬约束。

## Why HandyControl + LiveCharts2 推迟

| 库 | 推迟原因 | 何时引入 |
|----|---------|---------|
| HandyControl | WPF 原生 Button/DataGrid/Border 对功能完整的监控面板完全够用 | 所有功能稳定后，Phase 4 统一视觉 |
| LiveCharts2 | OxyPlot 能画所有需要的图（K线/折线/柱状/面积/散点），一个库解决 | OxyPlot 无法满足特定图表时 |

## 通信技术

| 通道 | 技术 | 用途 |
|------|------|------|
| 实时推送 | SignalR (WebSocket) | Tick/Portfolio/Strategies/Order/Alert 每秒推送 |
| 快照查询 | SignalR Hub Method | 替代 REST GET（单一协议） |
| 降级路径 | health.json 文件直读 | SignalR 断开时健康状态 |

**Why SignalR-only**: 服务端已实现 `EngineHubPushService` 每秒推送。Hub Method 原生支持 request-response（`hubConnection.InvokeAsync<T>`），不需要再维护 REST 轮询层。

## 关联文档

- [14 — WPF 监控客户端设计](14-wpf-monitoring-client-design.md)
- [12 — 实盘部署设计](12-live-deployment-design.md)
- [11 — 实施路线图 v2](11-implementation-roadmap-v2.md)
