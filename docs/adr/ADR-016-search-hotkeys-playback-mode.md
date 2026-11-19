# ADR-016：列表搜索、可配置与全局快捷键、播放模式、年份原文显示、媒体键、播放列表管理与单曲循环

- 状态：Accepted（2026-11-14 批准；2026-11-19 并入原 ADR-026，该 ADR 于 2026-11-17 批准）
- 日期：2026-11-14（2026-11-19 并入原 ADR-026）
- 相关：ADR-013（列表行次要信息）、ADR-014（schema v3 缓存修复，已批准）、ADR-019（标签编辑页）、
  ADR-021（文件操作）、ADR-025（标签规范化）、AGENTS.md §4 / §5 / §7
- 影响范围：`EchoGarage.Core`、`EchoGarage.Infrastructure`、`EchoGarage.App`、单元测试；
  不新增第三方依赖，不修改 SQLite schema

## 背景

### 列表 / 搜索 / 快捷键 / 播放模式（2026-11-14）

owner 在验收后提出四项需求：

1. 直接拖拽列表表头调整列宽后，程序不会记忆新宽度，必须进入“列与布局”保存才生效。
2. 需要可配置的快捷键，且可以选择“全局”（焦点在软件外时也生效）；当前只要求播放控制相关动作。
3. 需要搜索功能：支持文件名、标签、模糊搜索；默认只搜索当前浏览的播放列表。
4. 需要顺序播放 / 随机播放切换。

其中第 1 项是缺陷：分组视图拖拽宽度会即时写入 `CurrentLayout` 并保存，但关闭窗口时
`CaptureLayoutFromGrid()` 又从隐藏的平铺 `DataGrid` 读取旧列宽并覆盖；平铺视图的列宽则只在关闭时保存。
UAT 日志已实证（`Grouped column width: duration=65.0` 之后 `Persist layout ... captureFromView=True`
把 duration 覆盖回 62）。

### 年份显示 / 快捷键交互 / 播放列表 / 播放状态（2026-11-16）

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

## 决策

### 1. 列宽拖拽即时持久化（缺陷修复）

- 平铺 `DataGrid` 增加 `Thumb.DragCompletedEvent`（`handledEventsToo: true`）监听：拖拽结束、布局稳定后
  （`DispatcherPriority.Background`）调用 `CaptureLayoutFromGrid()` + `PersistCurrentLayout()`，不再等窗口关闭。
- `CaptureLayoutFromGrid()` 在分组视图激活时直接返回 `CurrentLayout.Clone()`：此时平铺表格隐藏、列宽陈旧，
  而分组拖拽宽度已经通过 `PersistGroupedColumnWidth()` 即时写入 `CurrentLayout`。关闭窗口时的捕获因此不会回退。
- 不改变持久化格式与 `PlaylistColumn` 模型。

### 2. 搜索

- Core 新增 `SearchQuery`（`Text` + `Fields: IReadOnlyList<SearchField>` + `Mode`）：
  `SearchField` 覆盖标题 / 艺术家 / 专辑 / 专辑艺术家 / 作曲家 / 流派 / 备注 / 文件名 / 路径 / 年份 / 格式；
  `SearchFieldMode.Any` 表示关键词可出现在所选任一字段，`All` 表示必须同时出现在所有所选字段。
  `SearchQuery.InlineFields` 固定为标题 / 艺术家 / 专辑 / 专辑艺术家，供列表上方的内联搜索框使用。
- Core 新增 `TrackSearch`（纯函数，可单测）：空白分词（支持双引号短语，最多 8 个关键词，查询截断到 200 字符）；
  每个关键词都必须命中（多关键词 AND）；命中判定为不区分大小写的**子串匹配**，不再使用模糊子序列
  （短关键词对长路径几乎总能凑成子序列，会产生大量无关结果；此前验证时已确认该问题并移除）。
- 内联搜索框：只匹配 `SearchQuery.InlineFields`，输入即过滤当前播放列表；高级搜索：`Ctrl+F`（可改键）打开
  `SearchWindow`，选择字段集合与“任一字段 / 全部字段”，确定后把关键词与字段限制一起应用，再次打开时回填当前条件。
- App 侧：`MainViewModel.FilterText` 经 180ms 去抖后设置 `_tracksView.Filter` 与 `_groupedTracksView.Filter`，
  只过滤当前播放列表已加载的行（不改变 `Tracks` 全量集合，行状态与封面缓存保留）；高级搜索通过
  `_advancedFields/_advancedMode` 参与同一过滤谓词，手动修改内联搜索框会自动回到默认字段。
  同时更新 `HasTracks`、空状态文案与副标题（内联“N / M 首 · 搜索“…””，高级“N / M 首 · 高级搜索”）。
  播放顺序取当前视图，因此过滤后只播放匹配行。
- UI：列表工具栏右侧搜索框（输入即过滤、`Esc`/✕ 清空、水印提示），空状态文案随过滤状态变化；
  `SearchWindow` 支持回车搜索、Esc 取消、“清除搜索”清空过滤。
- 诊断开关 `--search <text>`：启动后立即应用内联过滤，用于无键盘注入的截图验证；`--screenshot-all` 额外输出
  `dialog-search.png`（高级搜索对话框）。

### 3. 播放模式：顺序 / 随机 / 单曲循环

- `AppSettings.PlaybackMode`（Core `PlaybackMode` 枚举：`Sequential` 默认 / `Shuffle` / `RepeatOne`），持久化；
  三种模式互斥，以单一枚举表达，不另外并列 `bool RepeatOne` 开关。
- Core 新增 `ShuffleOrder`：Fisher–Yates 洗牌与“一轮”构建（当前曲目固定为第一首，其余洗牌；一轮内不重复）。
- `MainViewModel` 维护 `_shuffleOrder` 随机队列：切到随机时以当前播放行为起点重建；`PlayNext` 在随机模式取队列下一首，
  一轮结束自动重新洗牌（排除当前曲目）；`PlayPrevious` 在随机模式回退队列中的上一首；切换播放列表 / 搜索过滤时重置队列。
- 单曲循环：`IsRepeatOneEnabled`，三态标签 / Tooltip / 状态栏文案；播放模式按钮菜单增加“单曲循环”项，
  主按钮图标增加对应的“循环 + 1”图形（全部使用主题令牌）。
- 播放结束语义：
  - 顺序 / 随机播放走到末尾且没有下一首时，不再只停止播放器却保留“正在播放”面板，
    而是复用 `Stop()`：清空当前行、进度、标题与封面，等价于用户按下停止。
  - 自然播放结束（`PlaybackEnded`，无异常且非用户停止）时：`RepeatOne` 重新播放当前曲目；
    否则沿用原有逻辑进入下一首；无下一首时走上一条的停止分支。
  - 注意播放器的既有时序：自然结束先触发 `StateChanged(Stopped)`（`IsPlaying=false`、进度归零），
    再触发 `PlaybackEnded`；单曲循环重播会重新进入 `PlayRowAsync` 并恢复面板状态。
- UI：传输栏最右侧为播放模式图标按钮（顺序为向右箭头、随机为交叉箭头、单曲循环为“循环 + 1”图形），
  点击 / 右键弹出菜单选择“顺序播放 / 随机播放 / 单曲循环”，当前模式在菜单中打勾；三种状态使用同一套纯色图标，
  不做强调色高亮（彼此是平等关系）。音量区域同步压缩为“百分比 + 音量条”两个元素：点击百分比切换静音，
  静音时百分比标红并加删除线（数字大小不变），不再保留“音量”文字与独立喇叭按钮。

### 4. 年份原文显示（只读展示，不改变列表语义）

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

### 5. 可配置快捷键（含全局）、捕获式设定、媒体键与标签属性动作

#### 5.1 手势模型、全局注册与配置归一化

- Core 新增 `HotkeyAction`（PlayPause / Next / Previous / Stop / Mute / OpenSearch / OpenTagProperties）、
  `[Flags] HotkeyModifiers`、`HotkeyGesture`（解析 / 规范化 / `ToString()`）与 `HotkeyBinding`（Action + Gesture + Global）。
  解析规则：`Ctrl+Alt+Space`、`MediaPlayPause`、`Esc`、`F5` 等；修饰键别名 ctrl/control、alt、shift、win/windows/meta；
  键名大小写不敏感；输出统一为 `Ctrl+Alt+Shift+Win+Key` 顺序。
- `Hotkeys.cs` 提供媒体键 / 音量键与 VK ↔ 规范键名的纯逻辑辅助（`IsMediaKeyName` / `IsMediaGesture` /
  `IsMediaVirtualKey` / `KeyNameFromVirtualKey`），供捕获与钩子复用。
- `AppSettings.Hotkeys: List<HotkeyBinding>`；`JsonSettingsService.Normalize` 丢弃无效 Action、无法解析的手势、
  重复手势与重复动作，并写入规范化后的手势字符串。
- App 新增 `HotkeyService`：局部绑定在主窗口 `PreviewKeyDown` 中匹配（文本框内无修饰键的绑定不触发）；
  全局绑定用 Win32 `RegisterHotKey`（窗口句柄来自 `HwndSource`，`WM_HOTKEY` 经 hook 派发）。
  注册失败（组合被其它程序占用）只记录 `WARN` 并跳过，不影响启动。设置对话框保存后通过
  `MainViewModel.HotkeysChanged` 通知服务重新注册；窗口关闭时注销。
- `OpenSearch` 未配置时，主窗口 `Ctrl+F` 作为默认组合打开高级搜索；一旦用户为它配置了手势，则由配置的手势生效。
- `HotkeyService.Execute` 新增 `OpenTagProperties`：对 `MainViewModel.SelectedRow` 执行 `EditTagsCommand`
  （无选中行时忽略）。
- 不引入新依赖。

#### 5.2 捕获式设定与设置页总览

本文 2026-11-14 版本中的“手势输入框 + 输入即校验”交互已由捕获控件取代，最终交互如下：

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

#### 5.3 通用媒体键与默认映射

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

### 6. 播放列表管理

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

## 备选方案与取舍

- **快捷键**：改用第三方全局热键库会引入新依赖与许可证审查成本；`RegisterHotKey` 足够覆盖播放控制，
  且不需要低级键盘钩子。注册冲突无法自动解决，选择“记录日志 + 跳过”而不是打断用户。
- **搜索**：没有引入 SQLite FTS 或索引结构——默认范围只是当前播放列表的已加载行（通常几千首以内），
  内存过滤足够快；保持“文件是唯一真相”，不新增持久化索引。内联搜索只匹配四个主字段、使用子串匹配，
  避免“短关键词命中长路径 / 模糊子序列”带来的意外结果；需要按字段或跨字段查找时使用高级搜索对话框。
- **随机播放**：没有采用“纯随机下一首”，避免同一首歌在一轮内重复；没有实现完整播放历史栈，
  “上一首”在随机模式下只回退当前一轮内的前一首，边界处重播当前曲目。
- **把年份原文写入数据库**：会在文件标签变化后产生陈旧数据，违背“数据库只是缓存”；
  选择每次读标签时推导。
- **始终 `tag.Year = edit.Year` 并只写 4 位年份**：会不可逆地破坏详细日期（编辑标题也会触发），
  否决；改为仅在用户实际编辑年份时写入。
- **媒体键用 `WM_APPCOMMAND` / `RawInput`**：前者要求窗口在前台，无法实现全局；
  低级键盘钩子是唯一能同时满足“本地 / 全局 + 捕获”的方案。
- **保留文本框输入快捷键**：无法处理媒体键、容易输错；统一替换为捕获控件。
- **拖拽排序用上移 / 下移按钮**：交互成本高；选择直接拖拽。
- **用单独 `bool RepeatOne` 与顺序 / 随机开关并存**：三种模式互斥，合并进 `PlaybackMode` 枚举更清晰。

## 影响与风险

- 配置模型新增 `playbackMode` 与 `hotkeys` 两个字段；旧 `settings.json` 缺少字段时由默认值 / 归一化补齐，向后兼容。
- 全局快捷键需要程序保持运行；程序退出后系统注册自动注销。
- 搜索仅作用于当前来源（播放列表 / 媒体库）已加载的曲目，不跨播放列表搜索；高级搜索条件不持久化，
  关闭应用后恢复为无过滤状态。
- `HotkeyAction` 新增 `OpenSearch` 与 `OpenTagProperties`；旧配置不受影响（按名称反序列化，未知值由归一化丢弃）。
- 列宽修复不改变数据格式，只改变保存时机；分组视图关闭时不再用平铺表格覆盖宽度。
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

## 验证

### 初版（2026-11-14）

- 构建：`dotnet build EchoGarage.sln -nodeReuse:false -p:UseSharedCompilation=false` → 0 警告 0 错误。
- 测试：xUnit v3 进程内运行器 144 项全部通过（`TrackSearchTests` 覆盖内联四字段、高级单字段 /
  任一字段 / 全部字段、文件名 / 路径 / 年份 / 格式与空字段集；另有 `HotkeyGestureTests`、`ShuffleOrderTests`
  与设置归一化用例）。
- 截图：内联搜索默认字段（`--search anime` 因流派不在默认字段内显示“没有匹配项 · 共 5 首”，
  `--search dream` 命中标题）、高级搜索对话框（字段复选框 + 任一 / 全部字段 + 关键词与示例条件）、
  顺序 / 随机图标按钮、设置窗口快捷键段（含“打开高级搜索”行）。
- 日志：启动后 `Hotkeys loaded: local=1 global=1/1`，证明全局快捷键注册成功。
- 手工验证建议：拖动平铺 / 分组表头宽度后重启确认宽度保留；在其它程序获得焦点时按全局快捷键验证播放控制生效；
  按 `Ctrl+F` 打开高级搜索选择“作曲家 + 艺术家 / 任一字段”确认结果收敛，再次打开确认条件回填。

### 并入来源（原 ADR-026，已并入本文）的验证（2026-11-16 / 2026-11-17）

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

## 修订记录

- 2026-11-14：创建 `docs/adr/ADR-016-search-hotkeys-playback-mode.md`（本文件）：列表搜索、
  可配置（全局）快捷键、顺序 / 随机播放、列宽拖拽即时记忆；owner 批准。
- 2026-11-16：原 ADR-026 创建（`docs/adr/ADR-026-tag-year-hotkeys-playlists-repeat.md`，已并入本文）：
  年份原文显示、快捷键捕获式设定与媒体键、播放列表管理、单曲循环。
- 2026-11-17：原 ADR-026（已并入本文）经 owner 批准（Accepted）。
- 2026-11-19：合并：原 ADR-026（已并入本文）并入本 ADR，保留 ADR-016 编号与文件名；标题更新为覆盖两部分；
  决策按主题重组为「列宽 / 搜索 / 播放模式 / 年份原文 / 快捷键与媒体键 / 播放列表管理」；
  `PlaybackMode` 由 Sequential / Shuffle 补充为 `Sequential` / `Shuffle` / `RepeatOne` 三态；
  并入年份原文、播放列表管理等决策与相应备选方案、影响与验证；原 ADR-026 文件删除（内容已并入本文）。
