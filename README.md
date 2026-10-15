# QuickLaunch

Windows x64 常驻启动器，C# / .NET 10 / WPF。v0.1.3 已实现计划中的图标缓存、导入、拖拽、编辑、撤销、自启和主题跟随；面板只有单行分组页签和图标区，顶栏空白处可拖动窗口，启动项目或失去焦点后面板保持显示。性能目标与人工验收状态单独记录，不把“功能已实现”等同于“全部指标达标”。

## 使用

推荐使用 `artifacts/releases/` 中的 **standalone** 发布包，解压后运行 `QuickLaunch.exe`，无需另装 .NET。详细操作见 [使用说明](docs/QUICKSTART.zh-CN.md)。

- 默认 `Alt+Space` 呼出/隐藏，热键可在“设置”修改，冲突时保留旧组合。
- 拖动顶栏移动面板，拖动边缘或四角调整大小；位置和大小会自动记住，下次呼出恢复。设置 → 外观 → 呼出位置可切换“上次位置”或“跟随鼠标”。
- 顶栏左侧切换分组，滚轮可横向滚动；右侧添加项目图标、齿轮打开设置、× 隐藏面板。设置底部“退出 QuickLaunch”或托盘菜单“退出”可结束程序。
- 图标默认小尺寸（32 DIP），设置中可选小 / 中 / 大（32 / 40 / 48 DIP）；在图标区按住 Ctrl 滚动鼠标滚轮也可调整，自动保存。已记住的窗口大小保持不变，图标按新尺寸重排；未手动调整过的窗口自动适配。
- 删除、撤销、导入和错误显示为短暂提示条，导入期间可点击“取消”；成功启动、编辑和正常保存保持安静。设置中的“恢复默认位置和大小”会清除记忆并将可见面板移回当前屏幕的默认位置、自动适配大小。
- 添加项目时点击“应用 / 文件 / 文件夹 / 网址”类型卡片，表单随类型切换；文件夹可选常用位置，应用参数在“高级选项”中。也可从桌面、Explorer、浏览器拖入。
- 高 DPI Shell 图标，内存/磁盘缓存，右键可刷新；编辑时点击图标或右键“更换图标…”可选 60 个系统字形、8 种底色，或 ICO/PNG/JPG/BMP/EXE/DLL 文件。“恢复默认”还原目标图标。网址先显示地球，再异步替换为网站图标；自定义图标优先。
- 网站图标直接访问网址、跟随其 HTTP/HTTPS 重定向，并读取最终页面引用的图标（包括 CDN，不使用第三方图标查询服务）；设置 → 外观 →“自动获取网站图标”可关闭，关闭后不发请求、不使用已缓存的网站图标。成功缓存 30 天，失败缓存 1 天；右键“刷新图标”强制重新获取该站点。小图标居中显示在浅色圆角底板上，避免模糊放大。
- 条目排序、悬停标签跨组移动、拖到“+”新建组、标签排序、边缘滚动、预览/飞入动画；Esc 或窗外松开取消。
- 编辑名称/路径/参数/工作目录，打开所在位置，单条提权启动；删除后 5 秒内 Ctrl+Z 撤销最近一次删除。
- 分组新建、双击重命名、非空删除确认；呼出或鼠标切组后未选择，首次方向键/Home/End/PageUp/PageDown 选择首项，之后按行列或视口页移动选择。Enter 启动、F2 重命名、Delete 删除所选项；未选择时无操作。单击启动或点击图标区空白清除选择。Ctrl+Tab / Ctrl+Shift+Tab、Ctrl+1…9 切组并选择首项；Tab 在分组列表、标签和顶栏按钮间移动焦点。
- 跟随系统深浅色和强调色；面板在 Windows 11 22621 及以上且开启透明效果时使用 Acrylic，否则使用实体背景；高对比度使用系统颜色。设置/托盘可管理当前配置的登录自启。
- 单实例定向唤出、原子配置保存、备份恢复、损坏原件保留、未知版本只读。

## 配置

默认数据为 `%APPDATA%\QuickLaunch\config.json`；图标和日志在 `%LOCALAPPDATA%\QuickLaunch`。exe 同目录放置 `portable.flag` 可使用便携数据目录。`--config-dir <绝对目录>` 可选择独立配置。

脚本可用 `QuickLaunch.exe --exit [--config-dir <目录>]` 请求当前用户、当前登录会话中相同配置的实例正常退出：保存待处理修改并释放托盘图标，不需要强杀进程。退出码 `0` 表示实例已确认请求，`2` 表示没有实例确认（未运行或已退出）；确认不等于保存已完成，保存失败仍走原有重试/取消流程。省略目录时遵循默认/便携模式；此命令不会创建配置目录或启动新实例。

```powershell
$process = Start-Process '.\QuickLaunch.exe' -ArgumentList '--exit --config-dir "D:\LauncherProfile"' -PassThru -Wait
$process.ExitCode
```

修改后 500ms 防抖保存，退出时保存最后修改。顶栏只提示只读或尚未保存的错误；保存失败不会覆盖原文件，退出时可重试、放弃未保存修改或返回。另一个登录会话占用写锁时只读启动。

手工编辑 JSON 前先退出应用，暂不支持运行中热重载。单条删除撤销不涵盖整组删除。移动便携目录前应先关闭自启，移动后重新开启。

可选 `icon`：`glyph:E943:#2F6FDB` 或 `file:%USERPROFILE%\Pictures\icon.ico`（JSON 中反斜杠需转义）；EXE/DLL 可加资源索引，如 `file:C:\Windows\System32\shell32.dll,3`。未自定义时省略该字段；无效字形/颜色/格式恢复默认，图标文件丢失不影响启动。字形来自 Segoe Fluent Icons，无图片包；Windows 10 使用 Segoe MDL2 Assets 并隐藏不支持的字形。

## 开发

Windows 11 x64，仓库根目录 PowerShell：

```powershell
.\scripts\Setup.ps1                    # 安装仓库专用 SDK，验证 SHA-512
.\scripts\GenerateIcon.ps1             # 离线 WPF 生成应用 ICO 和 artifacts/icon/preview.png
.\scripts\Validate.ps1 -DesktopSmoke   # 锁定依赖、Release、逻辑与程序内窗口检查
.\scripts\Run.ps1                      # 正常运行
.\scripts\Run.ps1 -Silent              # 托盘运行，后台加载图标并预热当前组
.\scripts\Run.ps1 -ConfigDirectory 'D:\LauncherProfile'
```

SDK 10.0.401 和 NuGet 缓存位于忽略的 `.tools/`，不会替换系统安装。运行脚本启动 apphost exe，保留 manifest 中的 asInvoker/PerMonitorV2。自启若只依赖进程临时设置的开发运行时会被拒绝；使用独立包，或安装系统 .NET 10 Windows Desktop Runtime。

主题、系统强调色和标准控件使用 [WPF-UI 4.3.0](https://github.com/lepoco/wpfui/tree/4.3.0)（MIT），依赖版本与锁文件固定。`App.xaml` 合并其主题/控件字典，面板自定义样式直接引用库的主题资源；系统消息变化后重新应用主题与 Acrylic/实体背景策略。面板保留现有 WindowChrome；设置、项目编辑和名称对话框使用 FluentWindow、无图标标题栏和 Mica（回退由 WPF-UI 处理）。设置按快捷键、外观、启动分为卡片行；网站图标和登录自启使用开关，除快捷键需点“应用”外均立即生效。编辑高级选项使用 CardExpander，删除分组使用 WPF-UI 确认框。WPF-UI 滚动条占 14 DIP，布局为它保留空间。

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

日志和产物：`artifacts/tests/launcher.trx`、`artifacts/validation/smoke-result.json`、`artifacts/validation/window.png`、`artifacts/validation/settings.png`、`artifacts/validation/settings-light.png`、`artifacts/validation/edit-dialog.png`、`artifacts/validation/add-folder.png`、`artifacts/validation/name-dialog.png`、`artifacts/validation/delete-group.png`、`artifacts/benchmarks/`、`artifacts/startup/`、`artifacts/releases/`。对话框另有 `-light.png` 浅色快照；PNG 包含 WPF 标题栏，不含原生 DWM 材质或物理屏幕呈现证据。

当前结果、预算未达项和未验证场景见 [M2–M5 实施与验证](docs/validation/M2-M5.md)。[M0](docs/validation/M0.md) 和 [M1](docs/validation/M1.md) 是历史记录。
