---
name: 2026-06-11-ctp-cli-wrapper
description: CTP C++/CLI 封装完成 — MdApi+TraderApi 完整封装，替代 P/Invoke 旧方案
metadata:
  type: project
---

## 完成内容

### C++/CLI 封装层 (src/CTP/Wrapper/)

基于 ChatGPT 参考方案（`docs/CTP接口封装方案.md`），完整实现 CTP 6.7.13 SE x64 的 C++/CLI 封装：

- **17 个源文件**：5 DTO + 4 Native SPI + 4 Managed API + StringHelper + vcxproj + build.bat
- **编译输出**：CTPWrapper.dll (212KB) + 自动复制 thostmduserapi_se.dll / thosttraderapi_se.dll
- **C# 调用**：直接 `new CTP.MdApi()` / `new CTP.TraderApi()`，事件驱动，无需 P/Invoke

### 关键 Gap 修复

| Gap | 旧 C Bridge | 新方案 |
|-----|------------|--------|
| InstrumentID | ❌ | ✅ (reserve1) |
| TradingDay | ❌ | ✅ |
| 五档深度 | L1 only | ✅ L1-L5 |
| TraderApi | 无 | ✅ 下单/撤单/查持仓/查资金 |

### 技术要点

- CTP 6.7.13 结构体用 `reserve1`（非 `InstrumentID`）
- `#pragma managed(push, off)` 隔离 CTP 原生头文件
- `gcroot<T>` 在原生 SPI 中持有托管引用
- 编译后自动复制原生 DLL 到输出目录

### 清理

删除 13 个 P/Invoke 旧项目/文件：CtpInterop, Ct, Bridge, CtpTest, CtpLib, CtpFinal, CtpStandalone, PinvokeTest

### Demo

test/CtpDemo/ — 3 模式（md/td/all），C# 直接调用 C++/CLI

### 环境问题

系统 `C:\Program Files\dotnet` 缺失 host/fxr 和 Microsoft.NETCore.App.Ref
解决：安装到 `%USERPROFILE%\.dotnet` (10.0.301)，build.bat 配置 DOTNET_ROOT 指向那里
需管理员权限修复系统 dotnet（复制 host + packs）

### 项目结构

src/CTP/{SDK/, Wrapper/}
src/TradingStudio.Core/ (TickRecord)
src/TradingStudio.Ctp/ (CtpMdAdapter)
src/Scripts/ (10 Python)
test/CtpDemo/
