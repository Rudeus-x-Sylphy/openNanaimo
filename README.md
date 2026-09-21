# openNanaimo

面向韩国飞行射击游戏 **Nanaimo** 的客户端行为研究与本地协议适配工程，包含 Windows 图形启动器、完整 adapter 源码、构建脚本、验证脚本和工程知识库。

## 使用边界

1. 本仓库仅用于本地研究、兼容性分析和非营利体验，禁止盈利、对外运营或商业化使用。
2. 本仓库**不提供 Nanaimo 客户端**，不分发原始客户端可执行文件、动态库、安装包或完整资源包。使用者须自行准备合法取得且与基线一致的客户端和配套资源。
3. 仓库中的资源目录 JSON、预览图和兼容性数据用于本地研究，不代表原始开发方的授权或背书。
4. 项目与 Nanaimo 的原始开发者、发行方及任何相关组织均无隶属或合作关系。

## 当前完整 adapter

当前 GUI 不再区分“基础”和“扩展”adapter。社交、任务、商城、角色资料与原生地宫桥接统一由一套完整 adapter 启动：

```text
start_nanaimo_launcher.bat
  -> gui_launcher/nanaimo_launcher.ps1
  -> adapter_runtime/Nanaimo.Adapter.exe
  -> adapter_runtime/nanaimo_gameplay_bridge.exe
  -> game.exe
```

GUI 的主要入口是绿色按钮 **“一键进入 Nanaimo”**：保存角色配置、停止旧 adapter、校验文件、启动完整 adapter、注册角色资料并启动客户端。蓝色 **“仅启动适配器”** 只用于调试，不启动客户端。

## 功能范围

### 已合入完整 adapter

- 角色创建、资料、等级、经验、货币、外观、装备和库存。
- 宠物、宝石、宠物成长、技能、快捷栏、卡片与合成。
- 商城、Hans 金币商品、NaNa 商品、愿望清单、礼物和整容券。
- 任务卷轴、任务接取／放弃／完成和任务目标进度。
- 村庄移动、聊天、表情、好友、师徒、情侣。
- 玩家组队、玩家交易、卡片交易所。
- 天空竞技场、娱乐房间和相关大厅流程。
- 公寓、家具、室内商店和愿望清单。
- 地宫房间、多人同步、伤害、首领、掉落、结算、复活和关卡进度。

### 来源说明

- `latest.zip` 引入的业务玩法、社交、任务及对应数据层源码，来源作者记为 **蓝陌**。
- openNanaimo 对这些代码完成了一键启动整合、原生地宫状态桥接、宠物成长修复、Hans 商城修复、整容券修复、构建与验证补全。
- 普通怪物卡片掉率等数值策略调整属于 openNanaimo 自行修改，不归入蓝陌来源。

功能存在源码和自动检查，不等于每项都已经完成原客户端可见验收。证据边界见 [知识库](knowledge/知识库索引.md) 和 [完整 adapter 与客户端补丁指引](docs/完整适配器与客户端补丁指引.md)。

## 快速开始

### 1. 准备客户端

将自行准备的 Nanaimo 客户端和配套资源放在运行根目录。启动器要求：

| 文件 | 大小 | SHA-256 |
|---|---:|---|
| `game.exe` | 14,198,272 | `6E3985CB7BEBA0207DEB6201BFB05D01CF8D548D3B674D8E6BD6A6F2DEB72B90` |
| `Village_map_image/Village_map_image.pack` | 17,011,373 | `69EF0FA688DBCB4EE2A36151F76F78A5A3D4F051253ABE81EA824F37749FCD56` |
| `flying/hd0_ep22_dg00_st01.sstg` | 103,124 | `899B1820EC0F49E032589AEAAA582086D5471C4BB1982EFB104E96FEC74D6D27` |

仓库不提供这些客户端文件。完整依赖见 [运行依赖](docs/运行依赖.md)。

### 2. 准备 adapter 运行目录

发布包应包含：

```text
adapter_runtime/
  Nanaimo.Adapter.exe
  nanaimo_gameplay_bridge.exe
  adapter_manifest.json
  资源/数据/...
```

源码构建命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build_merged.ps1 `
  -TccPath '<TCC目录>/tcc.exe' `
  -DotnetPath '<.NET 8 SDK目录>/dotnet.exe' `
  -ResourceDataRoot '<已准备的adapter数据目录>' `
  -SelfTest
```

该命令生成 `adapter_runtime`，并运行隔离状态库、登录、资料注册、任务、商城、地宫桥接、宠物成长和持久化检查。`.NET 8 SDK` 用于构建，发布目标为自包含的 `net6.0/win-x64` 运行包。

### 3. 一键启动

双击：

```text
start_nanaimo_launcher.bat
```

在 GUI 中完成角色和资源设置后，点击 **“一键进入 Nanaimo”**。固定连接为本机回环：登录 `11005`、资料注册 `11999`、世界入口 `12050`。GUI 不提供外部地址或模式编辑入口。

## GUI 页面

| 页面 | 用途 |
|---|---|
| 启动配置 | 角色名、等级、称号、宠物、装扮和一键启动 |
| 本次启动详情 | 文件身份、配置摘要和启动动作 |
| 数值与道具 | HP/MP、攻击、防御、货币、钥匙、技能和快捷栏 |
| 宠物／装扮查表 | 资源编号、属性和图标预览 |
| 衣物／宠物／游戏道具／家具／卡片管理 | 本地库存编辑 |

运行数据写入 `adapter_data/`；日志位于 `adapter_data/logs/`。GUI 的“打开日志目录”直接打开该目录。

## 已修复的专项问题

### 宠物成长

原生地宫结算现在把宠物阶段、等级和经验纳入状态交换；正向角色经验结算会在同一事务中推进装备宠物，重复提交不会重复奖励。`CF72`、`CF88`、`C44C` 和 `C379` 使用同一持久状态。

### Hans 商城与整容券

- 补齐 18 个 Hans 定价商品，按商品币种分别扣除 Hans 或 NaNa。
- 支持客户端使用的 `C431` 付款模式 `0/2/3/4`。
- 一代／二代整容券按准确库存 identity 消耗，只修改允许的脸型范围，并通过 `C3D2`、`C47F` 即时刷新。

### 复活

复活蛋购买、激活、持久次数、`C355/CF71` 载体、death latch、`CF95 -> CF96/CF84/CF72` 顺序和重复请求幂等已通过 adapter 构造检查。原客户端复活后的可见位置与控制恢复仍需试玩确认，因此不写成端到端验收。

## 客户端补丁与资源准备

本仓库不提供客户端。需要自行处理的两个兼容点在 [完整 adapter 与客户端补丁指引](docs/完整适配器与客户端补丁指引.md) 中给出：

1. 家具选择：`VA 0x0041235F` 从旧目标 `0x005CE700` 改到原生 Index getter `0x005CEFE0`，字节 `E9 9C C3 1B 00 -> E9 7C CC 1B 00`。
2. 拉米诺斯村地宫7：完整 C355 前置进度、P03 道路 pack、关卡 SSTG 别名、限定资源查找映射和缺失 PON 同家族回退必须配套，不能只改一个显示标志。

## 构建与验证

```powershell
python -B scripts/verify_package.py --source-only
python -m unittest discover -s scripts -p 'test_*.py' -v
powershell -NoProfile -ExecutionPolicy Bypass -STA -File gui_launcher/nanaimo_launcher.ps1 -SelfTestLayout
```

原生 adapter 的确定性构建：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build_adapter.ps1 `
  -TccPath '<TCC目录>/tcc.exe' -KeepOutputs
```

当前源码清单和 reviewed build hashes 位于 `manifest/source_closure.json`。详细命令见 [构建与验证](docs/构建与验证.md)。

## 文档导航

| 文档 | 内容 |
|---|---|
| [知识库](knowledge/知识库索引.md) | 协议、状态、静态地址、实现机制和证据边界 |
| [完整 adapter 与客户端补丁指引](docs/完整适配器与客户端补丁指引.md) | 完整 adapter 架构、家具与拉米诺斯村地宫7准备步骤 |
| [启动流程与源码索引](docs/启动流程与源码索引.md) | GUI、资料注册、adapter 模块和端口 |
| [构建与验证](docs/构建与验证.md) | 构建、自测和发布校验 |
| [运行依赖](docs/运行依赖.md) | 客户端、资源、数据和工具依赖 |
| [CHANGELOG](CHANGELOG.md) | 本次合入、修复和验证记录 |
