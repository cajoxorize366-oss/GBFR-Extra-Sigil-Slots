# GBFR Extra Sigil Slots Standalone 架构

日期：2026-08-10
分支：`standalone-tauri`
基线：`cc60895997e496f57adde80878e2c9279d97440f` / `v0.8.3`

## 目标

独立版使用 Rust + Tauri 提供外部桌面界面，主动发现
`granblue_fantasy_relink.exe`，注入只包含游戏逻辑 Hook 和 IPC 的 x64 Native
Agent。独立版不依赖 Reloaded-II，不在游戏内渲染，不安装 DXGI Present、WndProc、
DirectInput 或 USER32 输入 Hook，也不包含 Dear ImGui、Overlay Broker 或 cimgui。

现有 Reloaded-II 版本继续保留；本分支新增独立产品，不把已有 Mod 原地改造成
单一加载方式。

## 模块

```text
GBFR.ExtraSigilSlots.Standalone/
  src/                         React + TypeScript 桌面 UI
  src-tauri/                   Rust 控制器、进程发现、注入、IPC、预设存储
  src-tauri/resources/agent/   发布时携带的 Native Agent 与 TSV 资源

GBFR.ExtraSigilSlots.Native/
  standalone_protocol.h        固定 little-endian 二进制协议
  src/standalone_agent.cpp     Bootstrap 与命名管道服务
```

## 生命周期

1. Tauri 启动后枚举目标进程，不自动修改其他同名进程。
2. 用户执行连接时，Rust 后端以目标 PID 和完整 EXE 名再次核验。
3. 若 Agent 已加载并且命名管道可连接，直接复用现有 Agent。
4. 否则通过 `OpenProcess`、远程 `LoadLibraryW` 注入 Native DLL。
5. Rust 通过本地模块中的导出偏移计算远程
   `GBFR20_StandaloneBootstrap` 地址，传入版本化启动参数。
6. Bootstrap 在 loader lock 外创建 worker；先选择 `input_hooks=false`，再调用现有
   `GBFR20_Initialize`。
7. 现有角色状态 owner-loop Hook 在其游戏线程上调用 `GBFR20_Tick`，保证状态重建不从
   任意控制器线程发起；IPC worker 只通过每 PID 唯一命名管道服务请求。
8. Tauri 关闭或断线不会卸载 Agent。Agent 保持可重连并随游戏进程退出，避免在活跃
   SafetyHook 回调期间远程卸载 DLL。

## 目录边界

- `module_directory`：Agent DLL 所在目录，只读，存放名称表和兼容性 TSV。
- `data_directory`：Tauri `app_data_dir` 下的可写目录，存放 NumConfig、pending 槽数、
  保护状态、预设和日志。
- Reloaded-II 路径未提供独立 data directory 时，继续使用原有 module directory，保证
  现有版本兼容。

## IPC

管道名：`\\.\pipe\GBFR.ExtraSigilSlots.Standalone.<pid>`。

协议为固定 frame header + command-specific payload；禁止把 native pointer、HMODULE、
ImGui context 或任意内存读写接口暴露给 WebView。单帧最大 8 MiB，长度、版本、命令和
结构尺寸均需验证。WebView 只调用 Tauri command，不能直接打开命名管道。

第一版命令：

| Command | Request | Response |
|---|---|---|
| `Hello` | empty | protocol、native ABI、PID、hook state |
| `GetState` | empty | `GBFR20_RuntimeState`、runtime message、pending slot count |
| `RefreshInventory` | empty | 有界库存数组和 UTF-8 label |
| `GetSelection` | character hash | 固定 24 个 slot id |
| `SetSelection` | character hash、virtual slot、inventory slot id | success/error |
| `ApplyPreset` | 最多 32 个角色的固定 24 槽选择 | slot result array |
| `RequestApply` | character hash | generation |
| `SetLanguage` | `zh-CN` / `en` | success/error |
| `RequestVirtualSlotCount` | 1-24 | pending/cleared/failed |
| `GetPendingVirtualSlotCount` | empty | 0 或 1-24 |

错误语义：协议错误、载荷错误、游戏状态拒绝、内部错误分别使用稳定状态码；错误响应不
改变选择或预设文件。断线和超时只影响外部 UI，不关闭游戏逻辑 Hook。

## UI 映射

独立窗口保留 v0.8.3 的信息和操作顺序：

1. 连接状态、目标 PID、语言切换、当前角色、刷新和扫描数量。
2. editable/read-only 状态与战斗中不能热更新的提示。
3. 当前生效槽数、pending 槽数、1-24 输入和缩减确认。
4. 当前预设、前后切换、套用、覆盖、另存为和管理。
5. 13 开始编号的扩展槽列表及清空操作。
6. 库存选择器：搜索、All/Used/Body/Extension/Unused 筛选、匹配数和虚拟列表。
7. 本体占用、扩展转移、预设名称、预设管理、预设转让和槽数缩减弹窗。

外部窗口不再需要 F8、鼠标释放门控、游戏光标接管和手柄直通逻辑。窗口关闭会退出
控制器，不改变游戏输入。

## 预设

Rust 端继续读写 schema v3 `GBFR-ExtraSigilSlots.presets.json`，固定保留 24 槽；保留
名称最大 48 字符、同角色名称不区分大小写唯一、损坏文件不覆盖、迁移前备份、原子写入、
清除引用后执行选择写入失败则回滚等现有语义。

## 兼容与安全边界

- 仅支持 x64 `granblue_fantasy_relink.exe`，注入前后都核验 PID 与映像名。
- 控制器与游戏完整性级别不一致时明确报错，不静默循环提权。
- Native semantic resolver、局部字节契约和 fail-closed 行为保持不变。
- Agent 不提供远程卸载；要移除 Hook，应退出游戏。
- 同一游戏进程只允许一个 Standalone Agent server；重复 bootstrap 必须幂等。
- 独立版与 Reloaded-II 版不能同时加载同一 Native Core，控制器应检测已加载模块并给出
  明确冲突提示。

## 动态 CI

Required：

- 现有 Native Release x64 构建与全部 smoke tests。
- Native standalone 导出、结构尺寸和 malformed bootstrap harness；Rust 端 frame、
  状态布局、长度上限与失效连接测试。
- `cargo fmt --check`、`cargo clippy --all-targets -- -D warnings`、`cargo test --locked`。
- `npm ci`、TypeScript typecheck、前端单测、production build。
- `tauri build`，并检查 bundle 只包含 Standalone 所需资源，不携带 Reloaded/ImGui DLL。
- 桌面与窄窗口截图、弹窗和长文本无重叠验证。

Advisory / 实机：

- Windows 10/11、普通/管理员游戏进程、首次注入、控制器重启重连。
- 未知命令、重复 bootstrap 与控制器抢占的实进程 IPC 验证。
- 游戏完全重启、装备界面、训练和任务状态、所有角色、预设冲突与槽数重启事务。
- 杀毒软件误报、Steam Overlay、ReShade、RTSS 等常见注入组合。
