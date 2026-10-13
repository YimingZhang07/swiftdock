# QuickLaunch

Windows 常驻启动器，C# / .NET 10 / WPF。当前已实现 **M1 基础功能**：热键与设置、托盘、单实例唤出、配置保存/恢复、窗口生命周期、分组与 Shell 启动。M0 的基本拖入和排序已接入持久化。

新增条目、排序、分组选择和热键设置现在会自动保存。真实图标缓存、完整导入/拖拽体验、条目编辑/删除撤销、自启和主题跟随仍按 M2–M5 实施。完整方案见仓库根目录的《QuickLaunch 启动器实现计划（C# + WPF）》；当前证据与待验证场景见 [M1 验证记录](docs/validation/M1.md)，[M0 记录](docs/validation/M0.md)保留为历史。

## 开发与运行

Windows 11 x64，在仓库根目录用 PowerShell 执行：

```powershell
# 首次使用：从微软官方源安装仓库专用 SDK，验证 SHA-512。
.\scripts\Setup.ps1

# 锁定依赖还原、Release 构建、测试。
.\scripts\Validate.ps1

# 启动；保持这个终端，使用面板或托盘的“退出”关闭。
.\scripts\Run.ps1
```

SDK 固定为 `10.0.401`，安装在被 Git 忽略的 `.tools/dotnet`。包缓存也放在 `.tools`，不会替换系统 .NET。直接使用 Visual Studio/系统 dotnet 时需安装满足 `global.json` 的 SDK。

- 默认 `Alt+Space` 显示/隐藏；通过面板或托盘的“设置”修改。新组合冲突时保留旧组合和配置，即使初始注册失败也可从托盘进入设置。
- 方向键/Tab 移动焦点，Enter 启动；单击条目先隐藏再启动。
- 拖动条目可以排序；悬停另一标签 400ms 切组，松开提交；Esc/窗口外松开取消。
- 从 Explorer 或浏览器开始拖动后可尝试热键呼出；更便于验证的入口是勾选“保持显示以添加”，再切换到源应用拖入。
- 右键条目可打开；应用类条目可单独请求管理员运行。启动器本身请以普通权限运行。
- 本阶段支持 FileDrop、Unicode URL、Mozilla URL/标题和纯文本；ANSI URL 仅接受 ASCII。网页标题缺失时采用域名。导入的详细格式写入诊断日志。
- `Run.ps1 -Silent` 启动到托盘。同一用户/登录会话/配置目录再次启动，会唤出已有窗口并退出第二个进程。

## 配置与保存

- 默认配置：`%APPDATA%\QuickLaunch\config.json`，备份为 `config.json.bak`。托盘可直接打开配置目录。
- exe 同目录存在 `portable.flag` 时使用便携目录；目录不可写会提供默认目录或只读选择。
- 可用 `Run.ps1 -ConfigDirectory 'D:\MyQuickLaunch'` 选择独立配置；直接运行 exe 时对应参数是 `--config-dir`。
- 修改后 500ms 防抖，UI 快照交给单一后台写入器，原子替换正式文件；正常退出会保存最后一次修改。顶栏显示“等待保存 / 保存中 / 已保存 / 尚未保存”。退出时保存失败可重试、放弃未保存修改或返回程序。
- 正式文件损坏时尝试备份；损坏原件先改名保留，恢复不覆盖唯一完好备份。更高版本配置只读，未知字段/缺失 ID 不静默丢弃。
- 另一个登录会话占用同一配置写锁时，当前实例提供只读启动功能，不保存计数、设置或排序。
- 若需手工改 JSON，**先退出启动器**，修改后重新启动；M1 尚未实现运行中配置热重载，应用内完整条目编辑留到 M5。
- 默认日志在 `%LOCALAPPDATA%\QuickLaunch\logs`；仓库运行脚本将诊断日志放到 `artifacts/m1`。日志按会话限额，保留最近的非占用会话文件。

## 验证与产物

```powershell
# 使用新建的隔离配置，短暂显示窗口和设置，验证第二实例转发，然后自动退出。
.\scripts\Validate.ps1 -DesktopSmoke

# 发布；目标机器需要 .NET 10 Windows Desktop Runtime x64。
.\scripts\dotnet.ps1 publish .\QuickLaunch\QuickLaunch.csproj -c Release -r win-x64 --self-contained false --no-restore --disable-build-servers '-m:1' '-p:PublishReadyToRun=false' -o artifacts/publish/m1
```

产物路径：

- `artifacts/tests/m1.trx`：37 项测试的结果。
- `artifacts/m1/smoke-result.json`：18 项 WPF/持久化/双进程检查报告。
- `artifacts/m1/m1-window.png`：WPF 渲染快照，用于布局检查，不是物理屏幕呈现测量。
- `artifacts/m1/quicklaunch-*.jsonl`：每次会话的有界诊断日志。
- `artifacts/publish/m1/`：framework-dependent 发布目录。

`Run.ps1 -Smoke` 总是创建新的测试配置，不能与 `-ConfigDirectory` 混用；直接传 `--smoke-test` 也必须明确指定独立测试目录。不会用日常配置做自动化修改。

脚本设置仓库内 Runtime 路径并运行 apphost exe，以应用其 asInvoker/PerMonitorV2 manifest。若直接双击发布 exe，Windows 需要能找到系统安装的 .NET 10 Windows Desktop Runtime；仓库 SDK 不会自动注册到系统。使用仓库 Runtime 测试发布 exe 时，先把当前终端的 `DOTNET_ROOT` 和 `DOTNET_ROOT_X64` 设置为仓库 `.tools/dotnet` 的绝对路径。

`show.ui-proxy` 只表示一次显示请求后的渲染回调和焦点检查完成，不表示真实按键到屏幕的延迟。当前测试也不替代真实浏览器 OLE、UAC 或混合 DPI 的人工验收。

如果在受限执行环境遇到热键错误 5、托盘 `TryCreate failed` 或测试宿主连接超时，在正常本机终端重试；不要为此把启动器改成管理员程序。普通桌面的热键占用与权限失败应按日志错误码区分。
