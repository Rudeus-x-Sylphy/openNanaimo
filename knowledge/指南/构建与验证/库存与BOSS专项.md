# 构建与验证／库存与BOSS专项

## 作用与执行条件

检查库存实例、卡片、BOSS显示的专项入口；任务显示边界见[地宫专项](地宫专项.md#任务系统定向检查)。命令均从仓库根目录执行；受测程序、工具链、输入和通过／失败／跳过结果分别登记。

<a id="df-14-016"></a>

<!-- FUSION BEGIN df-14-016 -->
## 库存与BOSS状态专项

```powershell
python -B scripts/test_boss_health_display.py
python -B scripts/test_split_boss_component_lifecycle.py
adapter_runtime/Nanaimo.Adapter.exe --inventory-protocol-self-test
adapter_runtime/Nanaimo.Adapter.exe --inventory-quickbar-self-test
adapter_runtime/Nanaimo.Adapter.exe --card-synthesis-self-test
```

任务系统定向检查见[地宫专项](地宫专项.md#任务系统定向检查)。

专项覆盖新增提示的初始化／增加／消耗／查看、奖励事务中的实例序号迁移、同代码多实例选中状态、SP专用成功与重试、宠物目录阶段上界，以及三分体BOSS的权威血条。完整启动式自检覆盖实际地宫工作进程、宠物状态往返与持久化。

<!-- FUSION END df-14-016 -->

返回[构建与验证目录](README.md)。

<!-- 保留术语修订前的章节链接 -->
<a id="库存与首领状态专项"></a>
<a id="构建与验证库存与首领专项"></a>

<!-- 迁移前的章节锚点；当前导航见本页。 -->
<a id="既有文档入口"></a>
