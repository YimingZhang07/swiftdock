# QuickLaunch

Windows 10/11 x64 常驻启动器，C# / .NET 10 / WPF。支持图标缓存、导入、拖拽、编辑、撤销、自启和主题跟随；面板只有单行分组页签和图标区，顶栏空白处可拖动窗口，启动项目或失去焦点后面板保持显示。

## 使用

发布提供安装版和便携版，均自带 .NET，无需另装运行时。详细操作见 [使用说明](docs/QUICKSTART.zh-CN.md)。

- **安装版**：运行 `QuickLaunch-<版本>-Setup.exe`，按向导选择安装路径。仅为当前用户安装，无需管理员权限；默认路径为 `%LOCALAPPDATA%\Programs\QuickLaunch`。开始菜单快捷方式自动创建，桌面快捷方式可选；完成时默认启动程序。安装包未签名，SmartScreen 若提示“Windows 已保护你的电脑”，确认来源后点击“更多信息”→“仍要运行”。
- **便携版**：将 `QuickLaunch-<版本>-Portable.zip` 解压到任意可写位置，打开其中的 `QuickLaunch\QuickLaunch.exe`。同目录的空 `Data\` 文件夹启用便携模式；配置、图标缓存、日志都保存在其中。移动时保留整个文件夹。
- **升级与卸载**：安装新版会沿用原路径并保留数据。可从 Windows“已安装的应用”卸载；卸载会移除该安装目录的登录自启项，并询问是否删除配置与缓存/日志，默认“否”。静默卸载始终保留数据。安装、升级和卸载会请求正在运行的程序正常退出；若无法退出，请从托盘退出后重试。便携版移除前先关闭自启并退出，再删除整个文件夹。

- 默认 `Alt+Space` 呼出并激活隐藏面板；可见且已激活或置顶时一按即隐藏，未置顶且未激活时先前置激活、再按隐藏。热键可在“设置”修改，冲突时保留旧组合。
- 顶栏图钉切换置顶，默认开启，立即生效并自动保存；只读配置不可修改。
- 托盘图标单击切换面板（未置顶且原本未激活时先前置），系统双击时间内的连点只切换一次；设置打开及切换页面均无滑入或淡入过渡。
- 拖动顶栏移动面板，拖动边缘或四角调整大小；位置和大小会自动记住，下次呼出恢复。设置 → 外观 → 呼出位置可切换“上次位置”或“跟随鼠标”。
- 顶栏左侧切换分组，滚轮可横向滚动；右侧依次为添加项目、设置、置顶、隐藏。设置底部“退出 QuickLaunch”或托盘菜单“退出”可结束程序。
- 设置 → 外观 →“透明度”按 5% 调整，100% 为完全透明，向左更不透明；原有材质设置保持不变。
- 设置 → 外观 →“缩放”按 10% 调整至 80%–150%（默认 100%，图标 40 DIP），只缩放面板的图标、文字和间距，并按比例调整、保存窗口大小；图标区 **Ctrl + 滚轮** 也可调整，设置及添加/编辑/图标选择对话框保持 Windows 正常大小。
- 删除、撤销、导入和错误显示为图标区下方的单行文字，过长时省略，导入期间可点击“取消”；成功启动、编辑和正常保存保持安静。设置中的“恢复默认位置和大小”会清除记忆并将可见面板移回当前屏幕的默认位置、自动适配大小。
- 添加项目时点击“应用 / 文件 / 文件夹 / 网址”类型卡片，表单随类型切换；文件夹可选常用位置，应用参数在“高级选项”中。也可从桌面、Explorer、浏览器拖入。
- 高 DPI Shell 图标，内存/磁盘缓存，右键可刷新；编辑时点击图标或右键“更换图标…”可选 60 个系统字形、8 种底色，或 ICO/PNG/JPG/BMP/EXE/DLL 文件。“恢复默认”还原目标图标。网址先显示地球，再异步替换为网站图标；自定义图标优先。
- 网站图标直接访问网址、跟随其 HTTP/HTTPS 重定向，并读取最终页面引用的图标（包括 CDN，不使用第三方图标查询服务）；设置 → 外观 →“自动获取网站图标”可关闭，关闭后不发请求、不使用已缓存的网站图标。成功缓存 30 天，失败缓存 1 天；右键“刷新图标”强制重新获取该站点。小图标居中显示在浅色圆角底板上，避免模糊放大。
- 条目排序、悬停标签跨组移动、拖到“+”新建组、标签排序、边缘滚动、预览/飞入动画；Esc 或窗外松开取消。
- 编辑名称/路径/参数/工作目录，打开所在位置，单条提权启动；删除后 5 秒内 Ctrl+Z 撤销最近一次删除。
- 分组新建、双击重命名、非空删除确认；呼出或鼠标切组后未选择，首次方向键/Home/End/PageUp/PageDown 选择首项，之后按行列或视口页移动选择。Enter 启动、F2 重命名、Delete 删除所选项；未选择时无操作。单击启动或点击图标区空白清除选择。Ctrl+Tab / Ctrl+Shift+Tab、Ctrl+1…9 切组并选择首项；Tab 在分组列表、标签和顶栏按钮间移动焦点。
- Esc 优先关闭已打开的右键菜单或取消拖动；否则有选择时只清除选择，焦点留在列表，下一次方向键重新选择首项；无选择时隐藏面板。
- 设置 → 外观支持主题（跟随系统 / 浅色 / 深色）、背景材质（亚克力 / 云母 / 纯色）和强调色（跟随系统 / 7 种 Windows 11 预设），立即应用并自动保存。高对比度优先；透明效果关闭或系统不支持时使用纯色，并在材质行说明原因。设置/托盘可管理当前配置的登录自启。
- 单实例定向唤出、原子配置保存、备份恢复、损坏原件保留、未知版本只读。

## 配置

默认数据为 `%APPDATA%\QuickLaunch\config.json`；图标和日志在 `%LOCALAPPDATA%\QuickLaunch`。exe 同目录存在 `Data\` 时使用便携模式：配置为 `Data\config.json`，缓存为 `Data\icons\`，日志为 `Data\logs\`，不向用户的 AppData 目录写入数据。`--config-dir <绝对目录>` 优先于便携模式，使用指定配置目录及默认缓存/日志目录；`--diagnostics-dir` 可单独覆盖日志路径。便携目录不可写时会提示选择默认目录、只读运行或退出。

外观为可选字段，默认值省略；旧 `iconSize` 的 32 / 40 / 48 自动映射为 `scale` 的 80 / 100 / 120，后续保存不再写 `iconSize`。只读配置可加载旧值但不写回，并禁用所有外观控件；Windows 高对比度始终覆盖所选主题、强调色和透明材质，关闭后恢复所选值。

| 字段 | 省略时 | 非默认值 |
| --- | --- | --- |
| `theme` | 跟随系统 | `light`、`dark` |
| `backdrop` | 亚克力 | `mica`、`solid` |
| `backdropOpacity` | 0（透明度 100%） | 0–100；界面透明度为 100 减此值 |
| `scale` | 100% | 80–150，10 的倍数；越界值夹紧并取最近一档 |
| `accent` | 跟随系统 | `blue`、`teal`、`green`、`purple`、`magenta`、`orange`、`slate` |
| `alwaysOnTop` | 置顶 | `false`（关闭置顶） |

强调色预设：蓝 `#0067C0`、青 `#038387`、绿 `#107C10`、紫 `#8764B8`、玫红 `#C239B3`、橙 `#CA5010`、石板灰 `#69797E`。亚克力保留 Windows 11 22621 起的支持条件；云母使用 WPF-UI 的系统支持检测，二者都遵守 Windows 透明效果与高对比度设置。材质应用失败也回退纯色，描述显示实际状态。

100% 时面板顶栏高 48 DIP、内容上方和左右内边距 16 DIP，底部预留 24 DIP 放置 12 DIP 状态文字，不覆盖图标；细分隔线、选中标签的 14×3 强调色下划线与首列图标对齐。上述尺寸随面板缩放变化，滚动条和原生窗口边框保持原尺寸；列均匀分配可用宽度，键盘导航和 Gong 插入位置与实际网格使用同一几何计算。


诊断日志使用 Serilog CLEF（UTF-8 JSONL）：事件名为 `EventId.Name`，结构化字段位于顶层。默认目录为 `%LOCALAPPDATA%\QuickLaunch\logs`，可用 `--diagnostics-dir` 覆盖；同一目录的多个进程共享按日/约 4 MiB 滚动的文件，保留 20 个。读取最新滚动文件及分段后，按 `Pid` 和 GUID `Session` 筛选会话；解析示例见 [日志说明](docs/LOGGING.md)。

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
.\scripts\Setup.ps1                    # 安装仓库专用 SDK 和便携 Inno Setup，验证下载
.\scripts\GenerateIcon.ps1             # 离线 WPF 生成应用 ICO 和 artifacts/icon/preview.png
.\scripts\Validate.ps1                # 锁定依赖、Release -warnaserror、快速单元测试
.\scripts\E2E.ps1                     # UI 改动后：解锁桌面，真实键鼠回归
.\scripts\Snapshots.ps1               # 按需：解锁桌面，生成视觉评审 PNG
.\scripts\Run.ps1                      # 正常运行
.\scripts\Run.ps1 -Silent              # 托盘运行，后台加载图标并预热当前组
.\scripts\Run.ps1 -ConfigDirectory 'D:\LauncherProfile'
```

SDK 10.0.401 和 NuGet 缓存位于忽略的 `.tools/`，不会替换系统安装。运行脚本启动 apphost exe，保留 manifest 中的 asInvoker/PerMonitorV2。自启若只依赖进程临时设置的开发运行时会被拒绝；使用独立包，或安装系统 .NET 10 Windows Desktop Runtime。

主题、系统强调色和标准控件使用 [WPF-UI 4.3.0](https://github.com/lepoco/wpfui/tree/4.3.0)（MIT），依赖版本与锁文件固定。`App.xaml` 合并其主题/控件字典，面板自定义样式直接引用库的主题资源；系统消息变化后重新应用已选主题、强调色与材质策略。`ApplicationThemeManager` / `ApplicationAccentColorManager` 生成深浅色强调色变体。面板保留现有 WindowChrome；设置、项目编辑、名称和确认对话框使用 FluentWindow、无图标标题栏和 Mica；选择纯色或触发系统回退时对话框也使用纯色。设置按快捷键、外观、启动分为卡片行；网站图标和登录自启使用开关，除快捷键需点“应用”外均立即生效。编辑高级选项使用 CardExpander，删除分组使用 WPF-UI 确认框。WPF-UI 滚动条占 14 DIP，布局为它保留空间。

验证分为三个独立层级：

| 命令 | 何时运行 | 产物 / 范围 |
| --- | --- | --- |
| `scripts/Validate.ps1` | 每次修改，包括沙箱 | 锁定依赖还原、应用与单测 Release 构建（`-warnaserror`）、快速单测；`artifacts/tests/launcher.trx`。 |
| `scripts/E2E.ps1` | UI 改动后，在解锁桌面 | 真实输入用例；`artifacts/e2e/e2e.trx`。 |
| `scripts/Snapshots.ps1` | 视觉评审时，在解锁桌面 | 独立 `QuickLaunch.Snapshots` 工具生成 51 张 PNG 和 `artifacts/validation/snapshots.json`，并检查深色设置文字对比度。 |

单测覆盖配置、VM、解析、图标、几何、资源和 DI 生命周期；E2E 检查原生输入与窗口连接；视觉布局由快照评审。默认验证不构建或运行桌面工具。测试与快照使用隔离配置，不修改日常配置或登录启动项。真实桌面要求见 [E2E 说明](docs/E2E.md)。

`--e2e-launch-log <file>` 必须配合独立 `--config-dir`，将条目启动委托替换为 JSONL 记录器，每行包含 `id`、`name`、`elevated`，不调用 Shell；未提供此开关时正常启动条目。

## 发布与性能检查

图标选择器提供简约（原有 60 个字形和 8 色）、系统（运行时读取 Windows 图标库）和彩色（96 个 Fluent UI System Icons）三组。彩色 SVG 内嵌于程序，按像素尺寸在后台渲染并缓存 PNG；系统图标使用资源 ID 保存。第三方许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)，该文件也嵌入单文件程序。

```powershell
.\scripts\Release.ps1                         # 验证、单文件发布、安装包、便携 ZIP、SHA-256
.\scripts\Release.ps1 -SkipTests              # 跳过单测验证
.\scripts\Release.ps1 -E2E                    # 额外运行桌面 E2E（需解锁桌面）
.\scripts\Benchmark.ps1 -Interactive          # 点击面板确认后进行 4×50 项测试
.\scripts\Benchmark.ps1 -SingleGroup -Prewarm  # 200 项、预热对照
.\scripts\Benchmark.ps1 -Prewarm -IdleSeconds 300
.\scripts\Benchmark.ps1 -Executable 'artifacts\staging\<版本>\QuickLaunch.exe' -Interactive
.\scripts\MeasureStartup.ps1 -Executable '<发布目录>\QuickLaunch.exe'
```

版本唯一来源是 `QuickLaunch/QuickLaunch.csproj` 的 `<Version>`；关于页、exe 版本、安装器和文件名均从它生成。发布新版本只需修改此字段，再运行 `Release.ps1`。每次构建先发布到 `artifacts/staging/<版本>/QuickLaunch.exe`，然后重建 `artifacts/release/<版本>/`，覆盖同版本的 `QuickLaunch-<版本>-Setup.exe`、`QuickLaunch-<版本>-Portable.zip` 和 `SHA256SUMS.txt`，不添加时间戳。`-E2E` 即使与 `-SkipTests` 同用也会运行 E2E。

发布使用自包含 win-x64 单文件，不含 PDB，不在 bundle 内压缩；安装器和 ZIP 各自压缩。`Setup.ps1` 将固定版本的 Inno Setup 7.1.0 安装到 `.tools/innosetup/`，下载时校验 SHA-256 和 Authenticode 有效签名。

基准会重复显示/隐藏窗口，使用专用生成数据，不启动条目。后台运行可能没有前台权限；这种情况下焦点确认失败单列，渲染耗时不能冒充完整呼出耗时。首次、预热后、闲置恢复及首次/已访问分组分开记录；不强制 GC 或修剪工作集。

`Snapshots.ps1` 输出 launcher、settings-general、settings-appearance、settings-panel、settings-about、add-dialog（应用）、add-dialog-file、add-dialog-folder、add-dialog-url、edit-dialog、name-dialog、icon-picker、icon-picker-system、icon-picker-color、item-menu、delete-confirm 的深/浅色 PNG，以及 `appearance-{dark|light}-{blue|purple}-{acrylic|mica|solid}.png`（12 张）、`appearance-settings-{dark|light}-{blue|purple}.png`（4 张）和 `panel-polish.png`。基础深色文件无后缀，浅色使用 `-light`。窗口不激活并固定在整个虚拟桌面外，确认框不进入模态循环，菜单/选择器不打开原生弹出窗口。每张图使用独立进程，6 秒超时后记录失败并继续；脚本默认 90 秒总运行时限（可用 `-TimeoutSeconds` 调整），超时终止渲染器及其工作进程。`snapshots.json` 在开始和每步结束时更新，始终记录 `rendered` / `failed`，以及运行中的 `current`；旧 PNG 不代表本次成功。PNG 为 WPF 合成图，不包含原生 DWM 材质、边框或阴影；原生材质由 E2E 验证。

性能日志保留在 `artifacts/benchmarks/`、`artifacts/startup/`，发布包在 `artifacts/release/<版本>/`。启动测量在无托盘的环境可显式使用 `MeasureStartup.ps1 -AllowUnavailableTray`（对应 `--allow-unavailable-tray`，仅性能模式有效）。基准禁用网站请求，避免网络时间影响测量。
