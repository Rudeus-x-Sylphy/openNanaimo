# openNanaimo

面向韩国飞行射击游戏 **Nanaimo** 的客户端行为研究与本地协议适配工程，包含 Windows 图形启动器、完整 adapter 源码、构建脚本、验证脚本和工程知识库。

## 使用边界

1. 本仓库仅用于本地研究、兼容性分析和非营利体验，禁止盈利、对外运营或商业化使用。
2. 本仓库**不提供 Nanaimo 客户端**，不分发原始客户端可执行文件、动态库、安装包或完整资源包。使用者须自行准备合法取得的客户端和配套资源；不同脱壳实现不会因为大小或哈希不同而被启动器拒绝。
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

GUI 的主要入口是绿色按钮 **“一键进入 Nanaimo”**：保存角色配置、停止旧 adapter、校验项目自带的 adapter 文件并确认 `game.exe` 存在、启动完整 adapter、注册角色资料并启动客户端。蓝色 **“仅启动适配器”** 只用于调试，不启动客户端。

## 功能范围

### 完整 adapter 功能

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

- 本次版本新增的任务、社交、交易、竞技、娱乐、公寓及相关 managed 数据层功能由 **蓝陌** 贡献。
- openNanaimo 完成统一启动整合、原生地宫状态桥接，以及宠物成长、Hans 商城、整容券等适配修复。
- 普通怪物卡片掉率由 1% 调整为 5%，属于本版本的数值策略调整。

功能存在源码和自动检查，不等于每项都已经完成原客户端可见验收。证据边界见 [知识库](knowledge/知识库索引.md) 和 [完整 adapter 与客户端补丁指引](docs/完整适配器与客户端补丁指引.md)。

## 快速开始

### 1. 准备客户端

将自行准备的 Nanaimo 客户端和配套资源放在运行根目录。启动器只要求 `game.exe` 文件存在，不限定其大小或 SHA-256，也不会检查由兼容补丁生成的村庄 pack、SSTG/PON 别名。

如需家具修复和地宫7资源链，请从自己的原始游戏目录派生一个独立覆盖目录：

```powershell
python -B scripts/prepare_client_compatibility.py `
  --source-root '<原始客户端目录>' `
  --output-root '<新建覆盖目录>' --all
```

该工具按 PE 节表定位家具调用点，按 `NANA_PACK` 结构改写地宫7道路，并从已有 SSTG/PON 复制兼容别名；它不以输入或输出哈希作为授权条件。仓库不提供这些客户端文件。完整依赖见 [运行依赖](docs/运行依赖.md)。
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

## 登录出生点与位置持久化

- 新角色首次进入仍走游戏原始的角色创建与新手引导流程，不预置到村庄页面。
- 完成引导后，角色的地图、村庄页面和坐标写入现有人物档案；下次登录按档案恢复到上次保存的位置。
- 正常离开或断开连接时保存当前 HP/MP、地图、页面和坐标，不把新角色的首次出生点逻辑与已有角色的续登逻辑混用。
## GUI 页面

| 页面 | 用途 |
|---|---|
| 启动配置 | 角色名、等级、称号、宠物、装扮和一键启动 |
| 本次启动详情 | 文件身份、配置摘要和启动动作 |
| 数值与道具 | HP/MP、攻击、防御、货币、钥匙、技能和快捷栏 |
| 宠物／装扮查表 | 资源编号、属性和图标预览 |
| 衣物／宠物／游戏道具／家具／卡片管理 | 本地库存编辑 |

运行数据写入 `adapter_data/`；日志位于 `adapter_data/logs/`。GUI 的“打开日志目录”直接打开该目录。

“一键进入 Nanaimo”会应用界面中的最新配置。对已有档案，人物等级会按界面值更新，经验同步到该等级下界；HP/MP、攻击、防御、货币、外观、装备宠物、技能和库存管理数据也会在启动前同步，同时保留上次下线位置。

## 已修复的专项问题

### 宠物成长

原生地宫结算现在把宠物阶段、等级和经验纳入状态交换；正向角色经验结算会在同一事务中推进装备宠物，重复提交不会重复奖励。`CF72`、`CF88`、`C44C` 和 `C379` 使用同一持久状态。

### Hans 商城与整容券

- 补齐 18 个 Hans 定价商品，按商品币种分别扣除 Hans 或 NaNa。
- 支持客户端使用的 `C431` 付款模式 `0/2/3/4`。
- 一代／二代整容券按准确库存 identity 消耗，只修改允许的脸型范围，并通过 `C3D2`、`C47F` 即时刷新。

### 复活

复活蛋购买、激活、持久次数、`C355/CF71` 载体、death latch、`CF95 -> CF96/CF84/CF72` 顺序和重复请求幂等已通过 adapter 构造检查。原客户端复活后的可见位置与控制恢复仍需试玩确认，因此不写成端到端验收。

## 客户端 bug 修复与资源准备

本仓库不提供客户端。使用者可采用自己的合法客户端与脱壳实现，并按 [完整 adapter 与客户端补丁指引](docs/完整适配器与客户端补丁指引.md) 修复以下两个已确认问题：

1. **家具选择崩溃 bug**：按 PE 节表确认调用点原字节后，在 `VA 0x0041235F` 将跳转目标由 `0x005CE700` 改为原生 Index getter `0x005CEFE0`，即把 `E9 9C C3 1B 00` 替换为 `E9 7C CC 1B 00`。
2. **拉米诺斯村地宫 7 进入兼容性 bug**：补齐 C355 低 44 位前置数据，并配套 P03 道路 pack、SSTG 名称兼容、资源查找映射和缺失 PON 同家族回退；该项不改写客户端二进制。

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
| [CHANGELOG](CHANGELOG.md) | 本版本功能、修复和验证记录 |
