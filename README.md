# QuickLaunch

Windows 常驻启动器，C# / .NET 10 / WPF。当前实现 **M0 桌面交互原型**：热键、托盘、两个示例分组、窗口定位、根容器鼠标捕获、组内/跨组拖动、外部拖入解析和 Shell 启动。

M0 的添加和排序只保留在当前会话。单实例、配置保存/编辑、真实图标缓存、完整拖拽动画、自启和主题跟随仍按后续里程碑实施。完整方案见仓库根目录的《QuickLaunch 启动器实现计划（C# + WPF）》；已验证与未验证项目见 [M0 验证记录](docs/validation/M0.md)。

## 开发与运行

Windows 11 x64，在仓库根目录用 PowerShell 执行：

```powershell
# 首次使用：从微软官方源安装仓库专用 SDK，验证 SHA-512。
.\scripts\Setup.ps1

# 锁定依赖还原、Release 构建、测试。
.\scripts\Validate.ps1

# 启动交互原型；保持这个终端，使用面板或托盘的“退出”关闭。
.\scripts\Run.ps1
```

SDK 固定为 `10.0.401`，安装在被 Git 忽略的 `.tools/dotnet`。包缓存也放在 `.tools`，不会替换系统 .NET。直接使用 Visual Studio/系统 dotnet 时需安装满足 `global.json` 的 SDK。

- `Alt+Space` 显示/隐藏；热键冲突时点击托盘显示，日志记录错误码。M1 才加入热键设置。
- 方向键/Tab 移动焦点，Enter 启动；单击条目先隐藏再启动。
- 拖动条目可以排序；悬停另一标签 400ms 切组，松开提交；Esc/窗口外松开取消。
- 从 Explorer 或浏览器开始拖动后可尝试热键呼出；更便于验证的入口是勾选“保持显示以添加”，再切换到源应用拖入。
- 右键条目可打开；应用类条目可单独请求管理员运行。启动器本身请以普通权限运行。
- 本阶段支持 FileDrop、Unicode URL、Mozilla URL/标题和纯文本；ANSI URL 仅接受 ASCII。网页标题缺失时采用域名。导入的详细格式写入诊断日志。
- `Run.ps1 -Silent` 启动到托盘。M0 不含单实例控制，避免同时运行多个原型占用热键。

## 验证与产物

```powershell
# 会短暂显示窗口，执行程序内冒烟检查并自动退出。
.\scripts\Validate.ps1 -DesktopSmoke

# 发布；目标机器需要 .NET 10 Windows Desktop Runtime x64。
.\scripts\dotnet.ps1 publish .\QuickLaunch\QuickLaunch.csproj -c Release -r win-x64 --self-contained false --no-restore --disable-build-servers '-m:1' '-p:PublishReadyToRun=false' -o artifacts/publish/m0
```

产物路径：

- `artifacts/tests/m0.trx`：逻辑测试结果。
- `artifacts/m0/smoke-result.json`：程序内 WPF 检查报告。
- `artifacts/m0/m0-window.png`：WPF 渲染快照，用于布局检查，不是物理屏幕呈现测量。
- `artifacts/m0/m0-*.jsonl`：每次会话的有界诊断日志。
- `artifacts/publish/m0/`：framework-dependent 发布目录。

脚本设置仓库内 Runtime 路径并运行 apphost exe，以应用其 asInvoker/PerMonitorV2 manifest。若直接双击发布 exe，Windows 需要能找到系统安装的 .NET 10 Windows Desktop Runtime；仓库 SDK 不会自动注册到系统。使用仓库 Runtime 测试发布 exe 时，先把当前终端的 `DOTNET_ROOT` 和 `DOTNET_ROOT_X64` 设置为仓库 `.tools/dotnet` 的绝对路径。

`show.ui-proxy` 只表示一次显示请求后的渲染回调和焦点检查完成，不表示真实按键到屏幕的延迟。当前测试也不替代真实浏览器 OLE、UAC 或混合 DPI 的人工验收。

如果在受限执行环境遇到热键错误 5、托盘 `TryCreate failed` 或测试宿主连接超时，在正常本机终端重试；不要为此把启动器改成管理员程序。普通桌面的热键占用与权限失败应按日志错误码区分。
