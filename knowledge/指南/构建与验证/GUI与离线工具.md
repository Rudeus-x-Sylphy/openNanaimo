# 构建与验证／GUI与离线工具

## 作用与执行条件

执行 GUI 自检和离线库存事务检查，限定实际客户端与服务的操作边界。命令均从仓库根目录执行；受测程序、工具链、输入和通过／失败／跳过结果分别登记。

<a id="df-14-013"></a>
<a id="8-gui-自测与-validate"></a>

<!-- FUSION BEGIN df-14-013 -->
<a id="8-gui-自测与离线校验"></a>

## GUI 自测与离线校验

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -STA -File gui_launcher/nanaimo_launcher.ps1 -SelfTestLaunchModes
powershell -NoProfile -ExecutionPolicy Bypass -STA -File gui_launcher/nanaimo_launcher.ps1 -SelfTestSocialMode
powershell -NoProfile -ExecutionPolicy Bypass -STA -File gui_launcher/nanaimo_launcher.ps1 -SelfTestLocalEntry
powershell -NoProfile -ExecutionPolicy Bypass -STA -File gui_launcher/nanaimo_launcher.ps1 -SelfTestPureNewPlayer
powershell -NoProfile -ExecutionPolicy Bypass -STA -File gui_launcher/nanaimo_launcher.ps1 -SelfTestLayout
powershell -NoProfile -ExecutionPolicy Bypass -STA -File gui_launcher/nanaimo_launcher.ps1 -SelfTestTitleIO
powershell -NoProfile -ExecutionPolicy Bypass -STA -File gui_launcher/nanaimo_launcher.ps1 -SelfTestCatalogPreview
powershell -NoProfile -ExecutionPolicy Bypass -STA -File gui_launcher/nanaimo_launcher.ps1 -ValidateOnly
```

七项 GUI 自测覆盖动态 Network 地址、本地复用现有 `127.0.0.1` 适配器、社交标签与 P1／P2／P3、纯新手档、布局、称号读写，以及宠物／装扮／装饰家具目录预览。`-ValidateOnly` 离线检查运行包身份、目录及预览素材、库存工具、配置、Network 模板和客户端启动参数；它不检查 `game.exe` 的存在性，也不连接适配器端口或启动客户端。家具预览验证覆盖目录图标路径、资源包键映射、ARGB1555 解码、图集坐标和资源缺失状态。

<!-- FUSION END df-14-013 -->

<a id="df-14-021"></a>

<!-- FUSION BEGIN df-14-021 -->
## 启动器响应性验证边界

- `scripts/test_launcher_responsiveness.ps1` 只加载生产函数定义，在隔离测试环境和实际 WinForms 事件循环中检查后台线程、界面回调、重复提交保护、失败恢复、连续任务及共享兼容准备的复用／失效，不启动或停止玩家客户端。
- `scripts/test_launcher_contract.py` 检查一次身份查询和单次多开准备的注册顺序。
- 实际操作会向 `adapter_data/logs/launcher-timings.jsonl` 记录总时长与阶段轮询采样。

普通“启动适配器”复用已保存配置，编辑变更须先显式保存。`launcher_tools=2`运行包启用会话常驻目录、库存行差异提交及资料／固定称号同事务的一份备份；备份按实际变更生成。GUI关闭、父进程退出或两分钟空闲会回收工具。独立`state grade/reset`命令采用独立备份契约。验证层级为离线事务与GUI构造，客户端体验按实机项目登记。

库存事务与目录复用隔离检查（可用`NANAIMO_TOOLS_EXE`指定候选运行包）：

```powershell
$env:NANAIMO_TOOLS_EXE=(Resolve-Path adapter_runtime/Nanaimo.Adapter.exe).Path
python -B scripts/test_launcher_incremental_save.py
```

专项覆盖SQLite实际写表／行、称号SQL失败整体回滚，以及含空格目录中的GUI请求复用。受测对象为隔离数据库与匹配版本的离线工具。
<!-- FUSION END df-14-021 -->

返回[构建与验证目录](README.md)。
