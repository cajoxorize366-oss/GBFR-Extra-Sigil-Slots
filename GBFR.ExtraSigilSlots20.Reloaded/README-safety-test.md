# 20 因子槽 direct battle hook 测试版 0.3.0-test7

这是 Reloaded-II 独立版测试包，不依赖也不会修改 Luma、ReShade 或游戏存档。

## 使用

1. 退出游戏；若已使用旧版，先单独备份旧目录中的 `GBFR-ExtraSigilSlots20.ini`。
2. 删除旧版 `GBFR.ExtraSigilSlots20.Reloaded` 文件夹后安装本包，不要覆盖混装；需要保留原选择时，再把刚才备份的 INI 放回新目录。本包故意不携带默认 INI，避免覆盖用户配置。
3. 通过 Reloaded-II 启动游戏，在装备界面按小键盘 `Num 8` 打开窗口。
4. 窗口打开期间，鼠标、键盘和手柄输入应全部被游戏隔离；点击窗口外也不应转动视角或操作游戏 UI。
5. 关闭窗口后，松开当前按键/摇杆；连续两帧检测到中立输入后才恢复游戏控制。

## 本轮重点测试

- test6 实测中 `Present TID` 与 `owner TID` 相同，却仍显示 `Battle Trait: rejected: owner thread`；原因是 Trait loop 执行时 TLS SLOT01 binding 已不再 active。该拒绝来自 Mod 自身状态机，不是游戏线程状态。
- test7 删除战斗注入路径上的 owner、SLOT01、任务阶段和 TLS 授权条件。只要 native status 的角色 hash 存在已保存选择，Trait category/apply loop 就直接从槽 13–20 读取对应 GemData。
- 自由训练、本地副本和联机副本使用完全相同的 direct-hook 规则。任务阶段仍禁止用户编辑，但不再阻止已有选择生效。
- 直接规则的已知取舍：同一客户端上若出现多个相同角色 hash 的 status，它们都可能读取同一份虚拟选择；Mod 仍不写 SaveData 或 `worn_by`。

- 装备界面 Q/E 切换本地角色后，顶部 `Equipment Q/E selected character` 应同步变化。
- 选择 13–20 槽因子后，Q/E 刷新一次游戏 UI，确认技能与实战效果生效。
- 将同一库存因子改选到另一角色的 13–20 槽，旧角色对应虚拟槽应自动清空。
- 将一个虚拟槽因子装备进游戏原生 1–12 槽，再点一次 `Refresh inventory`，原虚拟槽应自动卸下。
- 已被原生 1–12 槽占用的因子不应出现在选择列表；角色专属因子只应对指定角色显示。
- 从练习菜单进入自由战斗后，不修改任何槽位；窗口必须显示绿色 `Battle Trait: CONFIRMED`，context 1，并且 `N/N` 与所选虚拟因子数量一致。
- 只有 `Battle Trait: CONFIRMED` 才代表游戏自然的 Trait contribution loop 实际读取了虚拟因子；`Generation ... equipment/test rebuild copied` 不再作为战斗生效证据。
- 准备/加载阶段应显示 `mission locked`；普通副本实际战斗仍禁止编辑，但必须同样显示绿色 `Battle Trait: CONFIRMED`，已保存虚拟因子实际生效。
- 使用体力因子时，应以战斗中的最大生命值为最终判据；装备界面 73469，则自由训练和普通副本也必须为 73469，而不是 71669。
- 正常装备界面必须显示 `equipment editable`，不能再继承此前的 `mission locked`。
- 练习菜单应显示 `free-training editable`，切入自由战斗后保持可编辑。
- 在他人状态预览、联机等待房间和副本战斗中观察：本地虚拟因子不得注入他人的 status。

## 阶段诊断

窗口会显示 `UI/source mode`、编辑会话锁存、observed/authorized status、重绑次数及输入拦截状态。本版利用精确 status context 与已验证的状态转移区分场景：`1/1 + 当前 Q/E 角色 context 0` 建立装备编辑状态；练习菜单 `0/1` 单独建立自由训练编辑状态；准备/加载的无角色状态和副本菜单 `4/1` 锁定普通任务。进入 `context 1` 时只允许继承自由训练状态，不能继承装备编辑状态。进程若直接附加在无法判定的 `1/1 + context 1` 场景会保持只读。

请分别截取下列场景中的窗口顶部诊断信息：城镇装备、自由训练、联机等待房、投票未准备、投票已准备、加载副本、副本战斗、战斗中查看自己/他人状态。
