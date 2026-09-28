# openNanaimo
The September 27, 2026 integrated update repairs Dungeon 16 revival billing/effective HP-MP restoration, stage1-only P1..P16/R1..R7 title progression, exact CF16 title grades, and exactly associated kind60 laser damage. The launcher title selector now shows the decoded native grade0..24 names. Immediate inventory refresh and expiry controls remain included.


面向韩国的飞行射击游戏 **Nanaimo** 的客户端行为研究与本地协议适配工程，包含 Windows 图形启动器、完整 adapter 源码、构建脚本、验证脚本和工程知识库。

# 寻找拥有韩国Nanaimo客户端安装包的小伙伴
许多Nanaimo爱好者希望获取韩服Nanaimo的客户端（主要是其中包含的L8与高等级宠物的静态资源包）。遗憾的是，由于年代久远，该客户端安装包已从互联网及个人磁盘中遗失。在此恳请持有韩国Nanaimo客户端安装包的小伙伴能够分享出来。

# 한국 나나이모 클라이언트 설치 파일을 보유하신 분을 찾습니다
많은 나나이모 애호가분들이 한국 서버 나나이모 클라이언트(주로 L8 및 고레벨 펫을 포함한 정적 리소스 팩)를 구하고 있습니다. 안타깝게도 오랜 세월이 지나면서 해당 클라이언트 설치 파일은 인터넷과 개인 디스크에서 모두 유실되었습니다. 한국 나나이모 클라이언트 설치 파일을 보유하고 계신 분께서는 공유해 주시기를 간곡히 부탁드립니다.

# Call for Korean Nanaimo Client Installer
Many Nanaimo enthusiasts are eager to obtain the Korean server Nanaimo client (primarily for its static resource packs containing L8 and high-level pets). Unfortunately, due to the passage of time, the client installer has been lost from both the internet and personal drives. We hereby kindly request anyone who still has the Korean Nanaimo client installer to share it.

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

GUI 的主要入口是绿色按钮 **“一键进入 Nanaimo”**：保存角色配置、停止当前运行的 adapter 和客户端、校验完整 adapter 运行文件闭包，应用并验证下节列出的客户端补丁及兼容资源，随后启动完整 adapter、注册角色资料并启动客户端。蓝色 **“仅启动适配器”** 用于单独运行 adapter 调试入口。

## 启动时的客户端补丁

**“一键进入 Nanaimo”会直接修改磁盘上的 `game.exe`，并保存本地备份。** 当前自动应用以下补丁，适用范围由已登记的客户端布局与目标站点共同确定。

- 家具交互：重定向家具 Index 读取，避开拖动卡死路径。
- 结算手动确认：禁用两类结算控制器的自动倒计时，保留鼠标与键盘确认。
- 礼物刷新 HP/MP 显示：库存刷新时保留预览用 HP/MP 上限。
- 空地购房：接通正确类型的原生购房窗口，配合 C37B 余额让出补丁完成余额分发。
- 屋外装修：调整菜单、库存、面板、商店、资源、中文文本及保存生命周期。

升级时会将旧版**复活显示、P 弹恢复**兼容代码迁移回原生状态路径，并恢复鼠标确认分支。购房余额让出补丁保持启用。复活显示由一致的原生次数字段更新，连续关卡通过 PET 生命周期管理保留 P 状态。

地宫7另做**资源文件派生**（道路、关卡及 PON 别名），不属于该功能的 EXE 修改。完整修改范围、旧补丁迁移、适用边界与验证范围见 [完整 adapter 与客户端兼容说明](docs/完整适配器与客户端兼容说明.md)。

新手引导期间使用对应性别的基础初始外观；有效完成引导后，在正常进村流程恢复启动器配置的装备、特效和宠物。角色配置、库存与装备归属完整保留。阶段切换、状态保存和外观恢复已纳入自动化回归。

GUI 的 **“构建与启动纯新手档”** 会为每次启动创建唯一且隔离的本地账号与 runtime profile，初始状态固定为 1 级、零金币/NANA/SP、零库存和零任务进度，并由客户端原生建角页创建角色；角色创建后以 `TutorialCompleted=0` 进入引导。中性 profile 使用合法且已过期的可选权益日期，启动器确认核心服务就绪后再完成账号初始化。常规启动器档案保留原值。

## 功能范围

### 完整 adapter 功能

- 角色创建、资料、等级、经验、货币、外观、装备和库存。
- 宠物、宝石、宠物成长、技能、物品栏 Z/X 装备槽、快捷栏、卡片与合成。
- 商城、Hans 金币商品、NaNa 商品、愿望清单、礼物和整容券；NaNa头像购买只扣NaNa/Cash，并按C3CE的NaNa在前、Hans在后顺序刷新余额。
- 任务卷轴购买、接取／放弃／完成、`71000000..71000012` 主线链、地宫／技能／复活／背包／家具／怪物目标进度与事务化奖励。
- 村庄移动、聊天、表情、好友、师徒、情侣。
- 玩家组队、玩家交易、卡片交易所。
- 天空竞技场、娱乐房间和相关大厅流程。
- 单身公寓、按实例同步的家具选取状态、每日推荐及由 C397 驱动的推荐点即时刷新、14 天街区住宅与到期换房、房屋外观与标语牌拖入及文字编辑、中文装饰商店、金币及购物券结算、保存完成弹框。
- 启动器“数值与道具”页支持公寓推荐点数，默认1000；购房消费和日常获得的点数持久保存。
- 公寓使用规则与状态保存说明见 [单身公寓](docs/单身公寓.md)。
- 地宫房间、多人同步、伤害、首领、掉落、结算、复活和关卡进度。
- 普通怪物使用严格专属卡池，Boss 使用确认的房间卡池；生成、领取和库存提交保持单次且可重复检查。
- 频道切换保留村庄页面与最后合法坐标，并以连接代次隔离旧场景任务。

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
  scripts/        脚本（随仓库提供）
```

启动器会自动完成所需的客户端兼容准备；也可先手工执行只读预检：

```powershell
python -B scripts/prepare_client_compatibility.py `
  --source-root '<原始客户端目录>' `
  --output-root '<新建覆盖目录>' --furniture --native-state --dungeon-state --inventory-gift-display --dungeon7 --dry-run
```

兼容工具依据已登记的 PE 布局重定向家具 Index getter、将旧复活显示／P 弹兼容代码迁移至原生状态路径、关闭两类结算页的本地自动推进分支并保留手动输入，同时按 `NANA_PACK` 结构生成地宫7道路与 SSTG/PON 兼容别名。复活次数与跨关状态由 adapter 使用原生协议维护。站点校验、派生结果复核和 SHA-256 备份识别在同一流程完成。表情翻页由适配器端 C355 情侣姓名／戒指边界保障：空关系写入空姓名和零戒指，副本进度写入止于完整消息 `+0xDE`。C393 使用 1020 字节有界快照和 `info=2000` 终态；`info=6000` 表示继续分页。仓库提供适配工程文件，运行依赖见 [运行依赖](docs/运行依赖.md)。

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
| 数值与道具 | 最大 HP/MP、攻击、防御、货币、钥匙、技能和快捷栏 |
| 宠物／装扮查表 | 资源编号、属性和图标预览 |
| 衣物／宠物／游戏道具／家具／卡片管理 | 本地库存编辑 |

运行数据写入 `adapter_data/`；日志位于 `adapter_data/logs/`。GUI 的“打开日志目录”直接打开该目录。

“一键进入 Nanaimo”会应用界面中的最新配置。等级只用于新角色的初始种子；已有档案保留游戏中累计的等级和经验。最大 HP/MP、攻击、防御、货币、外观、装备宠物、技能和库存管理数据会在启动前同步；当前 HP/MP 由运行状态持久化，重新启动不会由 GUI 配置覆盖。地图、页面和坐标仍写入人物档案；新客户端进程的安全出生点、首次页面门控与位置持久化边界见 [完整 adapter 与客户端兼容说明](docs/完整适配器与客户端兼容说明.md#4-登录出生点与位置持久化)。

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

当前发布统一了背包实例、消费、扩充、卸装与战斗资源生命周期：购物券和普通道具按领域路由；快捷栏、丢弃、材料、通用使用和扩充券共享稳定的会话实例身份；城镇食物按实际 HP/MP 增量事务提交；外观零槽作为真实卸装状态持久化；GUI/DB 保存基础最大值，选中宠物宝石形成 effective 最大值，absolute current 跨库存刷新、结算、连续关卡、回城和重启保持一致。地宫位置交接保留合法页面与入口坐标，频道重入使用连接代次隔离旧状态；复活支付遵循玩家当次选择，死亡结果不会重建战斗场景；普通怪物和 Boss 卡牌使用可追溯的严格专属池。源码、runtime和运行清单配套发布，客户端派生组件统一覆盖家具、屋外装饰与地宫7。

## 文档导航

| 文档 | 内容 |
|---|---|
| [知识库](knowledge/知识库索引.md) | 当前协议、状态、接口边界与实现机制 |
| [完整 adapter 与客户端兼容说明](docs/完整适配器与客户端兼容说明.md) | 完整 adapter 架构、家具边界与拉米诺斯村地宫7兼容链 |
| [启动流程与源码索引](docs/启动流程与源码索引.md) | GUI、资料注册、adapter 模块和端口 |
| [构建与验证](docs/构建与验证.md) | 构建、自测和发布校验 |
| [运行依赖](docs/运行依赖.md) | 客户端、资源、数据和工具依赖 |
| [文件清单与导出工具](docs/文件清单与导出工具.md) | 导出器分层、候选清单与逐文件审核要求 |
| [第三方与许可说明](docs/第三方与许可说明.md) | 第三方组件与许可边界 |
