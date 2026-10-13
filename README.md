# QuickLaunch

Windows x64 常驻启动器，C# / .NET 10 / WPF。v0.1.2 已实现计划中的图标缓存、导入、拖拽、编辑、撤销、自启和主题跟随；顶部标题区可拖动窗口，启动项目或失去焦点后面板保持显示。性能目标与人工验收状态单独记录，不把“功能已实现”等同于“全部指标达标”。

## 使用

推荐使用 `artifacts/releases/` 中的 **standalone** 发布包，解压后运行 `QuickLaunch.exe`，无需另装 .NET。详细操作见 [使用说明](docs/QUICKSTART.zh-CN.md)。

- 默认 `Alt+Space` 呼出/隐藏，热键可在“设置”修改，冲突时保留旧组合。
- 添加 exe、lnk、文件、文件夹或 http/https 网址；也可从桌面、Explorer、浏览器拖入。
- 高 DPI Shell 图标，内存/磁盘缓存，右键可刷新；网址使用内置地球图标。
- 条目排序、悬停标签跨组移动、拖到“+”新建组、标签排序、边缘滚动、预览/飞入动画；Esc 或窗外松开取消。
- 编辑名称/路径/参数/工作目录，打开所在位置，单条提权启动；删除后 5 秒内 Ctrl+Z 撤销最近一次删除。
- 分组新建、双击重命名、非空删除确认；方向键、Enter、Ctrl+Tab / Ctrl+Shift+Tab、Ctrl+1…9、F2、Delete。
- 跟随系统深浅色和强调色；设置/托盘可管理当前配置的登录自启。
- 单实例定向唤出、原子配置保存、备份恢复、损坏原件保留、未知版本只读。

## 配置

默认数据为 `%APPDATA%\QuickLaunch\config.json`；图标和日志在 `%LOCALAPPDATA%\QuickLaunch`。exe 同目录放置 `portable.flag` 可使用便携数据目录。`--config-dir <绝对目录>` 可选择独立配置。

修改后 500ms 防抖保存，退出时保存最后修改。顶栏显示保存状态；保存失败不会覆盖原文件，退出时可重试、放弃未保存修改或返回。另一个登录会话占用写锁时只读启动。

手工编辑 JSON 前先退出应用，暂不支持运行中热重载。单条删除撤销不涵盖整组删除。移动便携目录前应先关闭自启，移动后重新开启。

## 开发

Windows 11 x64，仓库根目录 PowerShell：

```powershell
.\scripts\Setup.ps1                    # 安装仓库专用 SDK，验证 SHA-512
.\scripts\Validate.ps1 -DesktopSmoke   # 锁定依赖、Release、逻辑与程序内窗口检查
.\scripts\Run.ps1                      # 正常运行
.\scripts\Run.ps1 -Silent              # 托盘运行，后台加载图标并预热当前组
.\scripts\Run.ps1 -ConfigDirectory 'D:\LauncherProfile'
```

SDK 10.0.401 和 NuGet 缓存位于忽略的 `.tools/`，不会替换系统安装。运行脚本启动 apphost exe，保留 manifest 中的 asInvoker/PerMonitorV2。自启若只依赖进程临时设置的开发运行时会被拒绝；使用独立包，或安装系统 .NET 10 Windows Desktop Runtime。

`Run.ps1 -Smoke` 只使用新建的隔离配置，禁止与 `-ConfigDirectory` 混用。自动化检查不修改日常配置或真正的登录启动项。

## 发布与性能检查

```powershell
.\scripts\Publish.ps1                         # 独立版 + ZIP + SHA-256
.\scripts\Publish.ps1 -FrameworkDependent     # 依赖系统 Desktop Runtime
.\scripts\Publish.ps1 -ReadyToRun             # R2R 对照版本
.\scripts\Benchmark.ps1 -Interactive          # 点击面板确认后进行 4×50 项测试
.\scripts\Benchmark.ps1 -SingleGroup -Prewarm  # 200 项、预热对照
.\scripts\Benchmark.ps1 -Prewarm -IdleSeconds 300
.\scripts\MeasureStartup.ps1 -Executable '<发布目录>\QuickLaunch.exe'
```

基准会重复显示/隐藏窗口，使用专用生成数据，不启动条目。后台运行可能没有前台权限；这种情况下焦点确认失败单列，渲染耗时不能冒充完整呼出耗时。首次、预热后、闲置恢复及首次/已访问分组分开记录；不强制 GC 或修剪工作集。

日志和产物：`artifacts/tests/launcher.trx`、`artifacts/validation/smoke-result.json`、`artifacts/validation/window.png`、`artifacts/benchmarks/`、`artifacts/startup/`、`artifacts/releases/`。窗口 PNG 为 WPF 渲染快照，不是物理屏幕呈现证据。

当前结果、预算未达项和未验证场景见 [M2–M5 实施与验证](docs/validation/M2-M5.md)。[M0](docs/validation/M0.md) 和 [M1](docs/validation/M1.md) 是历史记录。
