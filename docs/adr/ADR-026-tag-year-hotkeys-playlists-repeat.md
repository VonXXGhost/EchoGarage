# ADR-026：年份原文显示、快捷键捕获式设定与媒体键、播放列表管理、单曲循环

- 状态：**Accepted**（owner 批准：2026-11-17）
- 日期：2026-11-16
- 关联：ADR-016（可配置 / 全局播放快捷键、顺序 / 随机播放）、ADR-019（标签编辑页）、
  ADR-025（标签规范化）、ADR-021（文件操作）、AGENTS.md §4 / §5 / §7
- 影响范围：`EchoGarage.Core`、`EchoGarage.Infrastructure`、`EchoGarage.App`、单元测试；
  不新增第三方依赖，不修改 SQLite schema

## 1. 背景

Owner 在验收后提出四组需求（2026-11-16）：

1. **年份显示**：部分文件的年份 Tag 写了详细日期（例如 m4a 的 `©day="2025.06.04"`、
   ID3 的 `TDRC="2023-05-12"`），但标签属性面板的“年份”栏只显示 4 位年份。
   要求属性面板显示原始值，而列表、分组等其它位置保持只显示年份。
2. **快捷键**：快捷键应由“点击按钮后直接按键捕获（Esc 退出捕获）”设定，而不是手输文本；
   设置页快捷键一栏要列出软件涉及的全部快捷键（包括转换预设等）；通用媒体键
   （上一首 / 下一首 / 播放暂停）此前不生效；另需增加“打开选中歌曲标签属性编辑框”的快捷键。
3. **播放列表**：媒体库右键菜单提供“新建播放列表 / 新建智能播放列表”；左侧播放列表顺序支持拖拽调整；
   保存布局预设若会覆盖同名配置，需要额外确认提示。
4. **播放状态**：歌曲自然播放结束且不会继续播下一首时（如顺序模式最后一首），
   自动切换到“无歌曲播放”（等价于按下停止）；播放模式增加“单曲循环”。

## 2. 决策

### 2.1 标签年份原文显示（只读展示，不改变列表语义）

- `AudioTagData`（Core）新增 `string? YearText { get; init; }`：保存从**文件**读取的年份日期原文；
  不写入 `library.db`（符合“文件是唯一真相、数据库只是缓存”），扫描 / 索引 / 重读时重新推导。
- `TagEditModel`（Core）新增 `string? YearText` 与 `bool YearChanged { get; set; } = true`。
  默认 `true` 保持既有调用方（含音频转换 `AudioConverter`）的“写入年份”行为。
- `TagBatchEditor.CreateEdit`：以源标签 `YearText` 初始化；仅当批量编辑的
  `Fields` 显式包含 `TagFieldKeys.Year` 时置 `YearChanged = true`，
  并同时写 `Year = ParseInt(year)`、`YearText = year.Trim()`。
  `GetFieldValues()` 的年份仍返回 4 位整数文本，避免改变标签规范化等既有数字语义。
- `TagLibTagService.Read`：新增 `ReadYearText(file)`，按文件实际标签类型依次尝试
  Apple `©day` → ID3v2 `TDRC` → `TYER` → Xiph `DATE` → `YEAR` → ASF `WM/Year` →
  回退 `tag.Year` 的 4 位字符串；读取异常只记 `WARN` 并回退。
- `TagLibTagService.WriteCore`：年份写入移入 `if (edit.YearChanged)`：
  - 先 `tag.Year = ResolveYearNumber(edit)`（`Year > 0` 用其值，否则取 `YearText` 前 4 位数字），
    保留纯年份标签（如 ID3v1）兼容性；
  - 再按默认标签类型写原始文本：Apple `SetText(©day)`（空则 `ClearData`）；
    ID3v2 非空先删除 `TYER` 再写 `TDRC`（空则删除 `TDRC` + `TYER`）；
    Xiph 写 `DATE` 并移除 `YEAR`（空则两者皆删）；ASF 写 `WM/Year`（空则移除）；
    `YearText` 为空但 `Year > 0` 时回退写 4 位年份文本（`ResolveYearText`），
    两个都为空才清除年份，避免调用方只传 `Year` 时被意外清空。
  - 未编辑年份时**完全不触碰**年份字段，编辑标题等其它字段不会把详细日期覆盖成纯年份。
- 音频转换复制标签：`AudioConverter.CopyTags` 改为调用新增的
  `AudioConverter.CreateTagCopy(AudioTagData, bool copyCover)`，复制 `YearText` 并置
  `YearChanged = true`，因此转换输出与源文件的年份原文一致（不再只写 4 位年份）。
- 标签属性窗口：`PopulateFieldValues` 对年份字段优先显示 `YearText.Trim()`，
  无原文时回退 `TagBatchEditor.GetFieldValues`；多文件去重 / `<多值>` / “保持不变”逻辑不变；
  `BuildResult` 仍只提交与初始值不同的字段，因此“打开后直接确定”不会改写年份。
- 列表、分组、排序、搜索仍使用 `Track.Year`（4 位），行为不变。
- 已知限制：TagLib 在 ID3v2.3 下把 `TDRC` 帧按 `TYER` 帧写盘（帧内仍保留完整文本），
  TagLib 自身可正确读回；第三方软件可能只识别前 4 位。FLAC 同时存在 ID3v2 与 Xiph 时
  按上述优先级取 ID3 值。

### 2.2 快捷键：捕获式设定、全量清单、通用媒体键与标签属性动作

- `HotkeyAction`（Core）新增 `OpenTagProperties`；`Hotkeys.cs` 增加媒体键 / 音量键与
  VK ↔ 规范键名的纯逻辑辅助（`IsMediaKeyName` / `IsMediaGesture` / `IsMediaVirtualKey` /
  `KeyNameFromVirtualKey`），供捕获与钩子复用。
- 新增 `HotkeyCaptureButton`（App 控件）：`Gesture` 为双向绑定依赖属性；
  点击进入“按下快捷键…（Esc 退出）”状态；纯修饰键等待；Esc 取消并恢复旧值；
  Backspace / Delete 清除；媒体键经键盘钩子捕获；失焦 / 窗口关闭 / `Unloaded` 自动退出。
- 设置窗口“快捷键”页改为三块：
  1. 7 个可配置动作（播放 / 暂停、上一首、下一首、停止、静音切换、打开高级搜索、
     打开标签属性）；
  2. 全部转换预设的快捷键（按预设名逐行显示，直接读写 `ConversionPreset.Hotkey/HotkeyGlobal`
     并即时保存）；
  3. 12 项只读内置快捷键：4 个默认媒体键（MediaPlayPause / MediaPreviousTrack /
     MediaNextTrack / MediaStop）+ Space / Enter / Alt+Enter / Delete / Ctrl+O / F5 /
     Ctrl+M / Ctrl+F，并标注“对应的可自定义动作留空 / 未设置时生效”以及媒体键的默认语义。
- 校验：动作行与预设行统一做 canonical 去重，冲突时提示“与…重复了”并回退到旧值；
  空手势勾“全局”被拒绝；预设空手势强制 `HotkeyGlobal=false`。
  `ConversionSettingsWindow` 的快捷键输入也改用同一捕获控件，并在保存前做预设内去重；
  捕获态按 Esc 只退出捕获、不关闭窗口。
- 内置回退快捷键语义：`Alt+Enter`（打开标签属性）、`Ctrl+M`（静音）、`Ctrl+F`（高级搜索）
  仅在用户未给对应动作配置手势时生效；一旦配置，内置组合让位。
  `Space` / `Enter` / `Delete` / `Ctrl+O` / `F5` 没有对应的可配置动作，保持无条件生效。
  注意 WPF 在按住 Alt 时把按键报告为 `Key.System` + `SystemKey`（例如 Alt+Enter 是
  `SystemKey.Enter`），内置回退判断必须先做与 `TryHandleLocal` 相同的归一化，
  否则未配置时内置 Alt+Enter 不会触发。
- **通用媒体键**：Windows 把媒体键以 `WM_APPCOMMAND` 投递，`PreviewKeyDown` 与
  `RegisterHotKey` 通常收不到，这是此前媒体键不生效的根因。新增
  `MediaKeyHookService`：`WH_KEYBOARD_LL` 低级键盘钩子（引用计数安装 / 卸载），
  捕获 `VK_MEDIA_*` / `VK_VOLUME_*`；捕获模式下独占交给 `HotkeyCaptureButton`，
  正常模式按下方优先级执行并通过返回值吞掉按键。媒体键不走 `RegisterHotKey`。
- **媒体键默认作用**（无需绑定即按系统媒体键的本意工作）：
  - 显式绑定优先：`Global` 绑定在任意前台窗口触发；本地绑定仅本进程窗口在前台时触发。
    若某媒体键已被显式绑定（任何作用域），该键不再走默认映射；本地绑定在应用非前台时放行给系统。
  - 未显式绑定的标准媒体键默认生效（Core `HotkeyDefaults`）：
    `MediaPlayPause` → 播放 / 暂停、`MediaPreviousTrack` → 上一首、
    `MediaNextTrack` → 下一首、`MediaStop` → 停止。
  - 默认动作仅在“本应用有活动播放会话（`CurrentRow` 非空，含暂停）”或
    “本进程窗口位于前台”时响应并拦截；后台且无会话时放行给系统，避免抢占其它播放器。
    无当前曲目时按上一首 / 下一首按“开始播放”处理（从选中项或列表首曲开始）。
  - 音量键无默认映射，未绑定时保持系统音量行为。
  - 默认媒体键需要钩子常驻，因此钩子改为随 `HotkeyService` 生命周期安装（启动即安装、
    `Dispose` 释放），而不再要求“存在媒体键绑定才安装”；设置页捕获期间另外增加引用。
- `HotkeyService.Execute` 新增 `OpenTagProperties`：对 `MainViewModel.SelectedRow`
  执行 `EditTagsCommand`（无选中行时忽略）。

### 2.3 播放列表管理

- 顶部“播放列表”菜单已移除，其内容迁移到左侧媒体库 / 播放列表导航的右键菜单
  （新建播放列表、新建智能播放列表、编辑智能条件、重命名、删除、从当前播放列表移除
  所选歌曲、添加所选歌曲到其它播放列表）；空白 / 分组标题处右键仅“新建…”可用。
- 媒体库右键菜单（`TrackMenu`）增加“新建播放列表…”与“新建智能播放列表…”，
  通过 `DataContext="{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource Self}}"`
  让菜单项正确绑定主视图模型命令；`ContextMenu` 的其余动态构建逻辑不变。
- 左侧播放列表拖拽排序：
  - 新增 `MainWindow.Playlists.cs` 分部类：左键按下记录候选项（默认播放列表不可拖动），
    超过系统拖拽阈值后启动 `DoDragDrop`；`DragOver` / `Drop` 根据落点在项的上 / 下半区、
    “播放列表”分组标题或列表尾部空白计算插入位置；2px 指示线用 `AccentBrush` 主题令牌绘制。
  - `MainViewModel.ReorderPlaylistsAsync`：校验节点数量 / 身份 / 连续性后，在
    `_suppressSourceSelection` 下用 `ObservableCollection.Move` 即时重排，调用仓储持久化，
    恢复选中来源；失败时记录日志、提示并重新加载来源。
  - Core 接口新增 `IPlaylistRepository.UpdatePlaylistOrderAsync(IReadOnlyList<long>)`；
    SQLite 实现在事务中按传入顺序写 `SortOrder`：默认播放列表固定 `0` 且永远在最前，
    其余播放列表连续 `1..N`；**不修改 schema**。
- 布局预设覆盖确认：
  - 顶栏“保存当前布局为预设…”发现同名预设时先弹确认框，取消则不保存；
  - “列与布局”窗口内保存同名预设同样确认后再替换。

### 2.4 播放状态与单曲循环

- `PlaybackMode`（Core）扩展为 `{ Sequential, Shuffle, RepeatOne }`。
- `MainViewModel`：`IsRepeatOneEnabled`、三态标签 / Tooltip / 状态栏文案；
  播放模式按钮的右键菜单增加“单曲循环”项，主按钮图标增加对应的“循环 + 1”图形
  （全部使用主题令牌）。
- 顺序 / 随机播放走到末尾且没有下一首时，不再只停止播放器却保留“正在播放”面板，
  而是复用 `Stop()`：清空当前行、进度、标题与封面，等价于用户按下停止。
- 自然播放结束（`PlaybackEnded`，无异常且非用户停止）时：
  - `RepeatOne`：重新播放当前曲目；
  - 否则沿用原有逻辑进入下一首；无下一首时走上一条的停止分支。
- 注意播放器的既有时序：自然结束先触发 `StateChanged(Stopped)`（`IsPlaying=false`、进度归零），
  再触发 `PlaybackEnded`；单曲循环重播会重新进入 `PlayRowAsync` 并恢复面板状态。

## 3. 备选方案（未采用）

- **把年份原文写入数据库**：会在文件标签变化后产生陈旧数据，违背“数据库只是缓存”；
  选择每次读标签时推导。
- **始终 `tag.Year = edit.Year` 并只写 4 位年份**：会不可逆地破坏详细日期（编辑标题也会触发），
  否决；改为仅在用户实际编辑年份时写入。
- **媒体键用 `WM_APPCOMMAND` / `RawInput`**：前者要求窗口在前台，无法实现全局；
  低级键盘钩子是唯一能同时满足“本地 / 全局 + 捕获”的方案。
- **保留文本框输入快捷键**：无法处理媒体键、容易输错；统一替换为捕获控件。
- **拖拽排序用上移 / 下移按钮**：交互成本高；选择直接拖拽。
- **用单独 `bool RepeatOne` 与顺序 / 随机开关并存**：三种模式互斥，合并进 `PlaybackMode` 枚举更清晰。

## 4. 后果与风险

- ID3v2.3 的详细日期实际写入 `TYER` 帧（TagLib 渲染行为），TagLib 自身读回正常；
  第三方工具可能只解析前 4 位。
- 音频转换复制标签时保留源文件年份原文（`CreateTagCopy`，`YearChanged = true`）；
  源文件只有纯年份（无详细日期）时按 4 位年份写入，不再清空年份。
- 低级键盘钩子仅 Windows 桌面有效；UAC 提权窗口在前台时钩子收不到媒体键；
  捕获期间媒体键被本进程独占，其它程序也收不到（预期）；若其它程序系统级独占媒体键，可能抢不到。
- 默认媒体键依赖常驻低级键盘钩子：钩子随程序启动安装，所有按键都会经过一次轻量回调
  （仅媒体键进入分发逻辑），带来极小的启动与输入开销；钩子安装失败时默认媒体键与
  媒体键绑定都失效（记 `WARN`），普通快捷键不受影响。
- 默认媒体键在“后台且无播放会话”时放行给系统，避免抢占其它播放器；若希望后台任意时刻
  控制本应用，需把媒体键显式绑定并勾选“全局”。
- 转换器页只检查预设之间的重复；预设与播放动作的跨页冲突会在应用设置时由
  `ConversionPresetFactory.NormalizeAll` 清理（可能静默清空预设热键）。
- 拖拽排序暂不支持拖到侧栏边缘自动滚动；分组虚拟化时可能找不到容器，仅影响指示线显示，
  排序结果仍正确。
- 设置页的只读内置快捷键清单与主窗口 `OnPreviewKeyDown` 是两处维护的信息，未来新增内置键需同步。

## 5. 验证

- 单元测试新增 / 更新：年份原文读写与 `YearChanged` 语义（`TagBatchEditTests`、
  `TagAndCoverTests` 的临时 ID3v2 回归）、播放模式序列化（`SettingsTests`）、
  媒体键解析与 VK 映射、默认媒体键映射（`HotkeyGestureTests`）、
  播放列表排序持久化（`RepositoryTests`）、转换标签复制
  （`ConversionTagCopyTests`：`CreateTagCopy` 字段 / 年份回退 / 空年份清除 /
  真实 FLAC→FLAC 转换集成，`TestTools` 提供 tools 解析与 NAudio AAC 容器构造）。
- 全量构建：`dotnet build EchoGarage.sln` 0 警告 0 错误；
  测试运行器：667 项全部通过（0 failed / 0 errors，含同批提交的排序方式调整测试）。
- 手动验证：属性面板显示详细日期且只改标题后日期保留；列表年份列仍为 4 位；
  快捷键捕获 / Esc / 清除 / 冲突回退；未配置动作时 Alt+Enter 打开标签属性、
  Ctrl+M 静音、Ctrl+F 高级搜索生效，配置其它手势后内置组合让位；
  媒体键在不绑定的情况下默认控制播放，媒体键本地与全局显式绑定覆盖默认；
  播放列表拖拽后重启顺序保持；同名预设覆盖确认；顺序模式最后一首播完回到“未在播放”；
  单曲循环重复当前曲目；左侧导航右键菜单各项启用状态正确；标签属性页显示文件路径；
  所有文本输入框（标签编辑 / 设置项 / 搜索框）内容起点紧凑且占位提示与光标对齐；
  转换输出保留源文件年份（含详细日期）。
