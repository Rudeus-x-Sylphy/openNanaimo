# openNanaimo
韩国飞行射击游戏 **Nanaimo** 的客户端行为研究与本地协议适配工程，包含 Windows 图形启动器、完整适配器源码、构建脚本、验证脚本和工程知识库。

[知识库](knowledge/README.md) · [变更记录](CHANGELOG.md)

## 使用边界

1. 本仓库仅用于本地研究、兼容性分析和非营利体验，禁止盈利、对外运营、商业化使用和未经授权的网络节点运营。
2. 本仓库**不提供 Nanaimo 客户端**，不分发原始客户端可执行文件、动态库、安装包或完整资源包。使用者须自行准备合法取得的基础客户端；韩服追加宠物、L7/L8及章节／四村挑战移植资源子集随仓库的独立资源ZIP提供，不能替代基础客户端。
3. 仓库中的资源目录 JSON、预览图和兼容性数据用于本地研究，不代表原始开发方的授权或背书。
4. 项目与 Nanaimo 的原始开发者、发行方及任何相关组织均无隶属或合作关系。

工程代码的许可证正文见 [LICENSE](LICENSE)。代码许可、项目用途声明与第三方素材权利按各自适用范围维护，说明见 [第三方与许可说明](knowledge/指南/第三方与许可说明.md)。游戏原始素材的使用与分发资格由其权利人授权决定。

README 的商业用途限制与 `LICENSE` 中 AGPL-3.0 授予的代码使用及分发权利存在尚未明确的适用范围问题。该用途声明不能直接解释为 AGPL-3.0 自带的非商业条款；其适用对象与关系仍需维护者明确。

## 完整适配器（adapter）

社交、任务、商城、角色资料与原生地宫桥接统一由一套完整适配器处理。启动器启动托管适配器，适配器按需调用原生桥接；进入游戏操作另行启动客户端：

```text
start_nanaimo_launcher.bat
  -> gui_launcher/nanaimo_launcher.ps1
  -> adapter_runtime/Nanaimo.Adapter.exe
     -> adapter_runtime/nanaimo_gameplay_bridge.exe（按需启动）
gui_launcher/nanaimo_launcher.ps1
  -> game.exe（点击“进入 Nanaimo”）
```

| 操作 | 作用 | 前提与档案行为 |
|---|---|---|
| 蓝色 **“启动适配器”** | 启动所选模式的适配器 | 先保存角色和资源设置 |
| 绿色 **“进入 Nanaimo”** | 校验运行文件、准备客户端兼容资源，注册所选档案并启动客户端 | 服务已就绪；本地已有账号续档，新名字复制当前管理档 |
| **“保存配置”** 或对应管理页保存 | 将角色数值及库存编辑写盘 | 写盘前须停止适配器 |

## 启动时的客户端补丁

**“进入 Nanaimo”准备客户端兼容组件时，会修改磁盘上的 `game.exe` 并保存本地备份。** 自动修改包括以下类型，适用范围由已登记的客户端布局与目标站点共同确定；完整集合见[客户端兼容](knowledge/工程/客户端兼容/README.md)。

- 新账号建角：按账号角色状态进入原生外观选择与角色命名界面。
- 家具交互：重定向家具 Index 读取，避开拖动卡死路径。
- 结算手动确认与经验显示：禁用两类结算控制器的自动倒计时，保留鼠标与键盘确认；只关闭战斗得分换算的临时经验，保留实时分数及结算奖励。
- 礼物刷新 HP/MP 显示：库存刷新时保留预览用 HP/MP 上限。
- 空地购房：接通正确类型的原生购房窗口，配合 C37B 余额让出补丁完成余额分发。
- 屋外装修：调整菜单、库存、面板、商店、资源、中文文本及保存生命周期。

### 旧补丁迁移

| 对象 | 升级后的行为 |
|---|---|
| 旧版复活显示、P 弹恢复兼容代码 | 迁移回原生状态路径；复活显示由一致的原生次数字段更新，连续关卡通过 PET 生命周期管理保留 P 状态 |
| 鼠标确认分支 | 恢复原分支 |
| 购房余额让出补丁 | 保持启用 |

### 地宫7兼容

| 对象 | 修改与条件 |
|---|---|
| 客户端 EXE | 修正小地图可用状态 |
| 道路、关卡和 PON 别名 | 执行资源迁移 |
| 入口 | 恢复到兼容基线 |
| 已登记的补制入口、标题和房间背景 | 仅移除摘要匹配的文件 |

资源修改与保留条件、旧补丁迁移和验证范围见[完整适配器与客户端兼容说明](knowledge/指南/完整适配器与客户端兼容说明.md)。

### 新手引导与纯新手档

新手引导期间使用对应性别的基础初始外观；有效完成引导后，在正常进村流程恢复启动器配置的装备、特效和宠物。角色配置、库存与装备归属完整保留。阶段切换、状态保存和外观恢复已纳入自动化回归。

纯新手档使用隔离的运行配置，操作顺序如下：

1. 在普通本地模式启动适配器。
2. 勾选 GUI 的 **“构建与启动纯新手档”**；绿色按钮变为 **“构建并进入纯新手档”**。
3. 点击绿色按钮，按下表选择或创建账号。

| 账号情况 | 结果 |
|---|---|
| 用户名为空 | 自动生成唯一账号；初次登录进入人物形象创建和角色命名界面 |
| 新账号名称 | 初次登录进入人物形象创建和角色命名界面 |
| 已有用户名 | 继续对应档案，保留已有角色存档值 |

初始数值及引导状态见[角色创建](knowledge/业务/角色/角色创建.md)和[新手引导](knowledge/业务/角色/新手引导/README.md)。

## 功能范围

### 完整适配器功能

- 角色创建、资料、等级、经验、货币、外观、装备和库存。
- 宠物、宝石、宠物成长、技能、物品栏 Z/X 装备槽、快捷栏、卡片与合成。
- 商城、Hans 金币商品、NaNa 商品、愿望清单、礼物和整容券。
- 任务卷轴购买、接取／放弃／完成、主线进度与奖励提交。
- 村庄移动、聊天、表情、好友、师徒、情侣。
- 玩家组队、玩家交易、卡片交易所。
- 天空竞技场、娱乐房间和相关大厅流程。
- 单身公寓、家具、住宅推荐、租期、屋外装修、装饰商店及保存；操作步骤见[单身公寓](knowledge/指南/单身公寓.md)。
- 地宫房间、多人同步、伤害、BOSS、掉落、结算、复活和关卡进度。
- 怪物及BOSS卡片掉落与库存提交。
- 频道切换、位置保存及旧连接状态隔离。

上述功能已有实现及相应自动检查；各项检查的覆盖范围和原客户端画面、交互的验收边界见[验证状态](knowledge/工程/验证状态/README.md)。具体数值、协议字段及业务规则从[知识库](knowledge/README.md)维护。

## 快速开始

### 0. 需要准备什么

| 项目 | 说明 |
|---|---|
| Windows | 启动器是 PowerShell + WinForms 脚本，系统自带的 Windows PowerShell 即可 |
| 托管启动工具 | 运行包中的`Nanaimo.Adapter.exe --tools`负责人物管理、迁移、客户端兼容准备及资源校验；启动与游戏运行不要求Python或.NET SDK |
| Nanaimo 客户端 | 自行合法取得；启动器读取**与仓库内容同一个根目录**下的 `game.exe`，缺失即拒绝启动，并需要 `flying/`、`Village_map_image/` 等原始资源 |
| `adapter_runtime/` | **已随本仓库提供**，无需自行构建 |

### 1. 准备客户端与目录

章节成套安装使用独立仓库目录作为安装源，在停止的完整CN客户端上执行：

```powershell
python -B scripts/deploy_story_release.py plan --client '<客户端根目录>'
python -B scripts/deploy_story_release.py apply --client '<客户端根目录>'
```

此入口需要Python 3.10以上及PyCryptodome，统一安装运行包、两端QT／QS、1,228项章节／四村资源、L7/L8前置资源和本地派生的客户端补丁，输出可恢复的事务收据。章节显式启用及当前包奖励审核见[章节自动部署](knowledge/指南/客户端二进制补丁/章节表与对白安装.md#自动部署)。常规启动仍使用目标目录内的启动器。

把本仓库内容放进客户端根目录（或把客户端的 `game.exe` 和资源目录放进仓库根），使两者同根：

```text
<客户端根目录>/
  game.exe
  flying/                 原始资源
  Village_map_image/      原始资源
  start_nanaimo_launcher.bat
  gui_launcher/           启动器
  adapter_runtime/        完整适配器（随仓库提供）
  scripts/                脚本（随仓库提供）
```

启动器会自动完成所需的客户端兼容准备；也可先手工执行只读预检：

```powershell
.\adapter_runtime\Nanaimo.Adapter.exe --tools compatibility `
  --source-root '<原始客户端目录>' `
  --output-root '<新建覆盖目录>' `
  --character-creation --furniture --native-state --dungeon-state `
  --inventory-gift-display --land-purchase --apartment-exterior `
  --apartment-recommendation --dungeon7 --dry-run
```

此命令选择与启动器相同的兼容操作，只校验和报告，不写入客户端或覆盖目录。实际启动时，工具按已登记的 PE 布局与补丁位置校验原始字节，生成兼容文件、保存 SHA-256 标识的备份并复核应用结果。完整修改范围见[客户端兼容](knowledge/工程/客户端兼容/README.md)，运行依赖见[运行依赖](knowledge/指南/运行依赖.md)。

### 韩服追加宠物与 L7／L8 资源（可选，资源本体已提供）

仓库的 [`resource-packs/nanaimo-korean-resource-kit.zip`](resource-packs/nanaimo-korean-resource-kit.zip) 提供配套韩服白名单输入与已转换L7/L8资源，旁边的`.sha256`用于校验。不需要另找韩服安装包；仍须自备匹配的基础CN客户端。下载仓库ZIP或正常克隆即可得到资源包，它不是Git LFS指针。

| 资源或阶段 | 内容与要求 |
|---|---|
| 追加宠物 | 英雄龙及121条追加宠物所需输入 |
| L7 | 原生外观和 BGM 字段补齐 |
| L8 | 两阶段及共享依赖 |
| PET 表 | 990条最终记录按本机基线合并，保留原有宠物与成长数据 |
| 安装准备 | 需要 Python 和 PyCryptodome |
| 正常游戏运行 | 不需要 Python |

**解压资源包不等于安装；不要整目录覆盖客户端。** 按[资源包安装步骤](knowledge/指南/韩服L7-L8与宠物资源说明.md#5-随仓库资源包与安装步骤)运行带基线校验及备份的安装器。

### 2. 适配器运行目录（已随仓库提供）

本仓库已包含完整的 `adapter_runtime/`：

```text
adapter_runtime/
  Nanaimo.Adapter.exe
  nanaimo_gameplay_bridge.exe
  adapter_manifest.json
  资源/数据/...
```

启动器启动前逐文件校验 `adapter_manifest.json` 记录的大小与 SHA-256。完整运行目录包含 `Nanaimo.Adapter.exe`、`nanaimo_gameplay_bridge.exe`、`Nanaimo.Adapter.dll`、`Nanaimo.Gameplay.dll` 及清单登记的全部必需文件。

开发者如需自行重建（可选）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build_complete_adapter.ps1 `
  -TccPath '<TCC目录>/tcc.exe' `
  -DotnetPath '<.NET 8 SDK目录>/dotnet.exe' `
  -ResourceDataRoot '<已准备的适配器数据目录>' `
  -TargetFramework net8.0 `
  -SelfTest
```

该命令生成 `adapter_runtime`，并检查隔离状态库、登录、资料注册、任务、商城、地宫桥接、宠物成长和持久化。构建使用`.NET 8 SDK`，当前发布为自包含的`net8.0/win-x64`运行包，可直接在Windows运行。脚本也支持net6.0目标，见[构建与验证](knowledge/指南/构建与验证/README.md)。完整仓库提供`adapter_runtime/资源/数据/`，构建默认读取该目录；`-ResourceDataRoot`可指定匹配的外部数据，独立源码导出包须配套准备当前PET等资源目录。

### 3. 一键启动

双击：

```text
start_nanaimo_launcher.bat
```

本地模式先显式保存角色和资源设置，点击 **“启动适配器”**，服务就绪后点击 **“进入 Nanaimo”**。本地入口为回环地址：登录`11005`、资料注册`11999`、世界入口`12050`。进入操作复用已保存档案与已有服务；社交模式使用检测或填写的特定局域网IPv4启动共享服务，再由P1／P2／P3注册并启动客户端，见[启动流程](knowledge/指南/启动流程与源码索引.md)。

## GUI 页面

| 页面 | 用途 |
|---|---|
| 社交模式 | 局域网地址、共享适配器与 P1／P2／P3 客户端入口 |
| 本地模式 | 角色名、等级、称号、宠物、装扮与本地启动 |
| 本次启动详情 | 文件身份、配置摘要和启动动作 |
| 数值管理 | 最大 HP/MP、攻击、防御、货币、钥匙、技能和快捷栏 |
| 宠物查表／装扮查表／装饰家具查表 | 资源编号、属性和图标预览 |
| 衣物箱管理／宠物箱管理／游戏道具管理／装饰家具管理／卡片管理 | 本地库存编辑 |

运行状态集中写入 `adapter_data/game.db`；日志位于 `adapter_data/logs/`。GUI 的“打开日志目录”直接打开该目录。旧档迁移与数据库失败处理见[持久化](knowledge/基础/持久化.md)。

本地档案选择、同名匹配、新档复制及保存步骤见[启动流程与源码索引](knowledge/指南/启动流程与源码索引.md)。地图、页面和坐标的持久化边界见[启动运行](knowledge/工程/启动运行/出生点与位置恢复.md#df-11-005)。

## 构建与验证

```powershell
python -B scripts/verify_package.py --source-only
python -m unittest discover -s scripts -p 'test_*.py' -v
powershell -NoProfile -ExecutionPolicy Bypass -STA -File gui_launcher/nanaimo_launcher.ps1 -SelfTestLayout
```

原生适配器的确定性构建：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build_adapter.ps1 `
  -TccPath '<TCC目录>/tcc.exe' -KeepOutputs
```

当前源码清单和已审核构建摘要位于 `manifest/source_closure.json`。详细命令见[构建与验证](knowledge/指南/构建与验证/README.md)。

库存实例与迁移见[物品实例](knowledge/业务/库存/物品实例/README.md)，HP/MP、复活与续关见[战斗资源](knowledge/业务/地宫/战斗资源/README.md)，掉落资格见[掉落](knowledge/业务/地宫/掉落/README.md)。发布身份与检查范围见[构建发布](knowledge/工程/构建发布/README.md)。

## 文档导航

| 文档 | 内容 |
|---|---|
| [知识库](knowledge/README.md) | 全部文档入口；基础机制、业务规则、工程事实与操作指南 |
| [操作指南](knowledge/指南/README.md) | 启动、配置、安装、构建、验证与回退步骤 |
| [完整适配器与客户端兼容说明](knowledge/指南/完整适配器与客户端兼容说明.md) | 适配器架构、家具边界与拉米诺斯村地宫7兼容链 |
| [启动流程与源码索引](knowledge/指南/启动流程与源码索引.md) | GUI、资料注册、适配器模块和端口 |
| [构建与验证](knowledge/指南/构建与验证/README.md) | 构建、自测和发布校验 |
| [运行依赖](knowledge/指南/运行依赖.md) | 客户端、资源、数据和工具依赖 |
| [文件清单与导出工具](knowledge/指南/文件清单与导出工具.md) | 导出器分层、候选清单与逐文件审核要求 |
| [第三方与许可说明](knowledge/指南/第三方与许可说明.md) | 第三方组件与许可边界 |

<!-- 兼容既有章节链接 -->
<a id="完整-adapter-功能"></a>
