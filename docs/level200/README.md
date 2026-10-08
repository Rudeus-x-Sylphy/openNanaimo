# 200级成长与发布合同

人物累计经验、内部桥接、客户端显示及启动校验采用配套实现。发布包含托管适配器、worker、客户端兼容配方、启动器与三份清单；CurveVersion3数据由匹配程序读取。验证层级归知识库09。

## 数据与边界

- `character_experience.csv` 改为201行 `index,total_experience`。T[index]是index+1级起点。
- KR整数曲线版本3；最大等级200，200级起点 **67,955,751,200=T[199]**，最大累计 **69,469,351,200=T[200]**。
- T[198]=66,463,315,200；199→200成本1,492,436,000。
- T[200]=69,469,351,200 是200级经验条上界；200级可继续积累1,513,600,000经验，不升201。
- 原99级表完整保存在 `character_experience_v2.csv`，仅用于离线迁移。
- 托管 `long`、原生 `unsigned long long`、SQLite INTEGER保存真实人物累计。
- 托管唯一投影 `CharacterProgression.ProjectClientExperience`；原生对应 `progression_project_client_experience`。
  普通等级 `(lower,current,next)=(0,total-T[L-1],T[L]-T[L-1])`，满级 `(0,total-T[199],1513600000)`。
  不一致的等级／经验、未知曲线版本或越界在投影前拒绝；满级显示值不回写存档。
- 宠物经验使用既有字段；单次奖励、两笔经验及倍率按知识库07执行。
- 原生记录version3/curve_version3写64位十进制；旧记录由离线工具一次性读入转换。
  新worker拒绝旧记录或错误长度，不猜测高32位，不默认覆盖损坏记录。
- 内部 F100/F102、F10B 的精确偏移、版本与长度见 `internal-layout.md`。

## 对外经验载体扫描及落点

各载体保持原帧长、校验、请求／周期条件及属性布局。

| 载体 | 生产构造/修正入口 | 32位显示字段 |
|---|---|---|
| C355/728 | `NetworkAdapterService.BuildLoadNecessityPayload`；`protocol/experience_carriers.inc` | 帧+0x28/+0x2C/+0x30 |
| CF71/184 | `DungeonProtocol.BuildRoomMember`；`protocol/experience_carriers.inc` | 帧+0x40 next、+0x44 current；lower须继承0 |
| C57C/52 | `game_session/managed_bridge.inc` 的F10B提交 | 帧+0x0C/+0x10/+0x14 |
| CF88 | `BuildDungeonEndGamePayload`、`PatchNativeCharacterProgressionFrame`、原生 `progression_progression_fill_record` | 每条记录+0x10/+0x14/+0x18；帧+0x0C起，步长0x34 |
| C59A任务结果 | `BuildTaskCompletionResultPayload` | payload+16/+20/+24 |
| 竞技结果 | `BuildArenaPvpResultRecord` | 统一投影后交竞技序列化器 |
| 娱乐室结果 | `NetworkAdapterService.Entertainment`结算记录 | 各成员按各自角色统一投影 |

CF88 record+0x0C仍为本次真实奖励。宽内部快照的差额只有合法旧奖励回放缺失显式奖励字段时才参与奖励计算；普通检查点的差额不授予经验、不窄化为u32。

C36A/112托管及原生构造共同改为：低6位不变，称号6位、等级8位、UID12位。校验称号与UID范围，不截断UID。不同成员使用自己的名字/UID/人物状态。

## 客户端兼容

城镇等级、入口条件、0～200级物品成长表及战斗三位等级排版采用精确二进制配方。启动器和独立连接器核对PE实际位点；应用、内嵌配方更新、校验与恢复统一见[客户端二进制补丁](../客户端二进制补丁.md)。

## 曲线迁移合同

旧99级曲线与原生记录通过一次性离线事务迁移到CurveVersion3。迁移预览包含全角色等级、经验、属性、快照及原生记录的前后值；提交核对审阅摘要、当前全行状态及备份，并在EXCLUSIVE事务内再次校验。人物经验、曲线版本、内部快照版本及人物原生经验记录同步更新，原属性保存于审计表。

旧99级满条映射到新99级区间的最后一个整数经验。已迁移记录保持CurveVersion3累计值，旧原生记录生成独立转换副本；原始记录作为恢复基线。待提交journal、在线账号、陈旧预览、等级经验不一致及未知布局均参与迁移准入检查。配套恢复使用同批程序与数据快照；64位累计是兼容读取的必要条件。

## 验证范围

自动化覆盖完整曲线、全部阈值、2^31／2^32、600亿、满级、多级、不同UID、64位桥接往返、格式校验、原生保存失败保护、去重、数据库重开、迁移幂等及补丁指令隔离执行。

客户端联合验收按登录与资料、城镇本人／他人、准备房／后加入、CF71级内投影、页面缓存、击杀与升级动画、成功／失败结算、死亡、续关、回城、换频道、任务奖励、重登、199→200及高等级多人称号分别登记。发布产物身份与已确认的验证层级由知识库09维护。
