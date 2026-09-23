# openNanaimo

面向韩国的飞行射击游戏 **Nanaimo** 的客户端行为研究与本地协议适配工程，包含 Windows 图形启动器、完整 adapter 源码、构建脚本、验证脚本和工程知识库。

## 使用边界

1. 本仓库仅用于本地研究、兼容性分析和非营利体验，禁止盈利、对外运营、商业化使用和未经授权的网络节点运营。
2. 本仓库**不提供 Nanaimo 客户端**，不分发原始客户端可执行文件、动态库、安装包或完整资源包。使用者须自行准备合法取得的客户端和配套资源。
3. 仓库中的资源目录 JSON、预览图和兼容性数据用于本地研究，不代表原始开发方的授权或背书。
4. 项目与 Nanaimo 的原始开发者、发行方及任何相关组织均无隶属或合作关系。

## 完整适配器（adapter）

社交、任务、商城、角色资料与原生地宫桥接统一由一套完整 adapter 启动：

```text
start_nanaimo_launcher.bat
  -> gui_launcher/nanaimo_launcher.ps1
  -> adapter_runtime/Nanaimo.Adapter.exe
  -> adapter_runtime/nanaimo_gameplay_bridge.exe
  -> game.exe
```

GUI 的主要入口是绿色按钮 **“一键进入 Nanaimo”**：保存角色配置、停止当前运行的 adapter 和客户端、校验完整 adapter 运行文件闭包，从使用者自备文件派生并验证家具 Index 重定向及第五村地宫7兼容资源，随后启动完整 adapter、注册角色资料并启动客户端。表情翻页由适配器端有界 C355 情侣字段保障；家具快照由固定 C393 契约提供，点击路径由本地 Index getter 重定向处理。蓝色 **“仅启动适配器”** 用于单独运行 adapter 调试入口。

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

源码与自动检查覆盖上述功能；原客户端可见验收范围单独记录在 [知识库](knowledge/知识库索引.md) 和 [完整 adapter 与客户端兼容说明](docs/完整适配器与客户端兼容说明.md)。

## 快速开始

### 0. 需要准备什么

| 项目 | 说明 |
|---|---|
| Windows | 启动器是 PowerShell + WinForms 脚本，系统自带的 Windows PowerShell 即可 |
| Python 3 | 一键启动会调用 `scripts/prepare_client_compatibility.py` 现场派生客户端兼容覆盖；仓库优先使用 `tools/python/python.exe`，否则使用 PATH 中的 `py.exe` / `python.exe`，都没有时会直接报错 |
| Nanaimo 客户端 | 自行合法取得；启动器读取**与仓库内容同一个根目录**下的 `game.exe`，缺失即拒绝启动，并需要 `flying/`、`Village_map_image/` 等原始资源 |
| `adapter_runtime/` | **已随本仓库提供**，无需自行构建 |

### 1. 准备客户端与目录

把本仓库内容放进客户端根目录（或把客户端的 `game.exe` 和资源目录放进仓库根），使两者同根：

```text
<客户端根目录>/
  game.exe
  flying/                 原始资源
  Village_map_image/      原始资源
  start_nanaimo_launcher.bat
  gui_launcher/           启动器
  adapter_runtime/        完整 adapter（随仓库提供）
```

启动器会自动完成所需的客户端兼容准备；也可先手工执行只读预检：

```powershell
python -B scripts/prepare_client_compatibility.py `
  --source-root '<原始客户端目录>' `
  --output-root '<新建覆盖目录>' --furniture --dungeon7 --dry-run
```

该工具按 PE 节表验证并局部重定向家具 Index getter，按 `NANA_PACK` 结构生成地宫7道路，并从现有 SSTG/PON 派生兼容别名。表情翻页由适配器端 C355 情侣姓名／戒指边界保障：空关系写入空姓名和零戒指，副本进度写入止于完整帧 `+0xDE`。C393 使用 1020 字节有界快照和 `info=2000` 终态；客户端仅在 `info=6000` 时续页。哈希用于本机诊断和备份识别。仓库仅包含适配工程文件，运行依赖见 [运行依赖](docs/运行依赖.md)。

### 2. adapter 运行目录（已随仓库提供）

本仓库已包含完整的 `adapter_runtime/`：

```text
adapter_runtime/
  Nanaimo.Adapter.exe
  nanaimo_gameplay_bridge.exe
  adapter_manifest.json
  资源/数据/...
```

启动器启动前逐文件校验 `adapter_manifest.json` 记录的大小与 SHA-256 闭包。完整运行目录包含 `Nanaimo.Adapter.exe`、`nanaimo_gameplay_bridge.exe`、`Nanaimo.Adapter.dll`、`Nanaimo.Gameplay.dll` 及清单登记的全部 critical files。

开发者如需自行重建（可选）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build_complete_adapter.ps1 `
  -TccPath '<TCC目录>/tcc.exe' `
  -DotnetPath '<.NET 8 SDK目录>/dotnet.exe' `
  -ResourceDataRoot '<已准备的adapter数据目录>' `
  -SelfTest
```

该命令生成 `adapter_runtime`，并运行隔离状态库、登录、资料注册、任务、商城、地宫桥接、宠物成长和持久化检查。`.NET 8 SDK` 用于构建；发布目标是自包含的 `net6.0/win-x64` 运行包，运行该发布包只需 Windows。构建输入还包括 `-ResourceDataRoot`（`资源/数据`：客户端同名数据文件与派生的 `dungeon_combat_catalog.bin`），该数据由构建者自行准备。

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

“一键进入 Nanaimo”会应用界面中的最新配置。等级只用于新角色的初始种子；已有档案保留游戏中累计的等级和经验。HP/MP、攻击、防御、货币、外观、装备宠物、技能和库存管理数据会在启动前同步，同时保留上次下线位置。

## 登录出生点与位置持久化

- 新角色首次进入仍走游戏原始的角色创建与新手引导流程，不预置到村庄页面。
- 完成引导后，角色的地图、村庄页面和坐标写入现有人物档案；下次登录按档案恢复到上次保存的位置。
- 正常离开或断开连接时保存当前 HP/MP、地图、页面和坐标，不把新角色的首次出生点逻辑与已有角色的续登逻辑混用。
- C365/C367/CB21 中的 `FFFF/FFFF` 表示场景切换／活动哨兵。适配器转发对应帧并保留最后合法坐标；数据库中的 `65535,65535` 与 `1023,1023` 异常值在加载时保留地图／页面并归一化为 `(400,96)`。

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
| [完整 adapter 与客户端兼容说明](docs/完整适配器与客户端兼容说明.md) | 完整 adapter 架构、家具边界与拉米诺斯村地宫7兼容链 |
| [启动流程与源码索引](docs/启动流程与源码索引.md) | GUI、资料注册、adapter 模块和端口 |
| [构建与验证](docs/构建与验证.md) | 构建、自测和发布校验 |
| [运行依赖](docs/运行依赖.md) | 客户端、资源、数据和工具依赖 |
| [文件清单与导出工具](docs/文件清单与导出工具.md) | 导出器分层、候选清单与逐文件审核要求 |
| [第三方与许可说明](docs/第三方与许可说明.md) | 第三方组件与许可边界 |
