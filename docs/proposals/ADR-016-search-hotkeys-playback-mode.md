# ADR-016：列表搜索、可配置（全局）播放快捷键、顺序 / 随机播放与列宽即时记忆

- 状态：提案（待 owner 批准）
- 日期：2026-11-14
- 相关：ADR-013（列表行次要信息）、ADR-014（schema v3 缓存修复，仍在待批）

## 背景

owner 在验收后提出四项需求：

1. 直接拖拽列表表头调整列宽后，程序不会记忆新宽度，必须进入“列与布局”保存才生效。
2. 需要可配置的快捷键，且可以选择“全局”（焦点在软件外时也生效）；当前只要求播放控制相关动作。
3. 需要搜索功能：支持文件名、标签、模糊搜索；默认只搜索当前浏览的播放列表。
4. 需要顺序播放 / 随机播放切换。

其中第 1 项是缺陷：分组视图拖拽宽度会即时写入 `CurrentLayout` 并保存，但关闭窗口时
`CaptureLayoutFromGrid()` 又从隐藏的平铺 `DataGrid` 读取旧列宽并覆盖；平铺视图的列宽则只在关闭时保存。
UAT 日志已实证（`Grouped column width: duration=65.0` 之后 `Persist layout ... captureFromView=True`
把 duration 覆盖回 62）。

## 决策

### 1. 列宽拖拽即时持久化（缺陷修复）

- 平铺 `DataGrid` 增加 `Thumb.DragCompletedEvent`（`handledEventsToo: true`）监听：拖拽结束、布局稳定后
  （`DispatcherPriority.Background`）调用 `CaptureLayoutFromGrid()` + `PersistCurrentLayout()`，不再等窗口关闭。
- `CaptureLayoutFromGrid()` 在分组视图激活时直接返回 `CurrentLayout.Clone()`：此时平铺表格隐藏、列宽陈旧，
  而分组拖拽宽度已经通过 `PersistGroupedColumnWidth()` 即时写入 `CurrentLayout`。关闭窗口时的捕获因此不会回退。
- 不改变持久化格式与 `PlaylistColumn` 模型。

### 2. 可配置快捷键（含全局）

- Core 新增 `HotkeyAction`（PlayPause / Next / Previous / Stop / Mute / OpenSearch）、`[Flags] HotkeyModifiers`、
  `HotkeyGesture`（解析 / 规范化 / `ToString()`）与 `HotkeyBinding`（Action + Gesture + Global）。
  解析规则：`Ctrl+Alt+Space`、`MediaPlayPause`、`Esc`、`F5` 等；修饰键别名 ctrl/control、alt、shift、win/windows/meta；
  键名大小写不敏感；输出统一为 `Ctrl+Alt+Shift+Win+Key` 顺序。
- `AppSettings.Hotkeys: List<HotkeyBinding>`；`JsonSettingsService.Normalize` 丢弃无效 Action、无法解析的手势、
  重复手势与重复动作，并写入规范化后的手势字符串。
- App 新增 `HotkeyService`：局部绑定在主窗口 `PreviewKeyDown` 中匹配（文本框内无修饰键的绑定不触发）；
  全局绑定用 Win32 `RegisterHotKey`（窗口句柄来自 `HwndSource`，`WM_HOTKEY` 经 hook 派发）。
  注册失败（组合被其它程序占用）只记录 `WARN` 并跳过，不影响启动。设置对话框保存后通过
  `MainViewModel.HotkeysChanged` 通知服务重新注册；窗口关闭时注销。
- `OpenSearch` 未配置时，主窗口 `Ctrl+F` 作为默认组合打开高级搜索；一旦用户为它配置了手势，则由配置的手势生效。
- UI：设置窗口新增“快捷键”段（六行固定动作 + 手势输入框 + “全局”复选框），输入即校验并保存。
- 不引入新依赖。

### 3. 搜索

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

### 4. 顺序 / 随机播放

- `AppSettings.PlaybackMode`（Sequential 默认 / Shuffle），持久化。
- Core 新增 `ShuffleOrder`：Fisher–Yates 洗牌与“一轮”构建（当前曲目固定为第一首，其余洗牌；一轮内不重复）。
- `MainViewModel` 维护 `_shuffleOrder` 随机队列：切到随机时以当前播放行为起点重建；`PlayNext` 在随机模式取队列下一首，
  一轮结束自动重新洗牌（排除当前曲目）；`PlayPrevious` 在随机模式回退队列中的上一首；切换播放列表 / 搜索过滤时重置队列。
- UI：传输栏最右侧新增顺序 / 随机图标按钮（顺序为向右箭头、随机为交叉箭头），点击后在按钮上方弹出菜单选择“顺序播放 / 随机播放”，
  当前模式在菜单中打勾；两个状态使用同一套纯色图标，不做强调色高亮（两者是平等关系）。音量区域同步压缩为“百分比 + 音量条”两个元素：点击百分比切换静音，
  静音时百分比标红并加删除线（数字大小不变），不再保留“音量”文字与独立喇叭按钮。

## 备选方案与取舍

- **快捷键**：改用第三方全局热键库会引入新依赖与许可证审查成本；`RegisterHotKey` 足够覆盖播放控制，
  且不需要低级键盘钩子。注册冲突无法自动解决，选择“记录日志 + 跳过”而不是打断用户。
- **搜索**：没有引入 SQLite FTS 或索引结构——默认范围只是当前播放列表的已加载行（通常几千首以内），
  内存过滤足够快；保持“文件是唯一真相”，不新增持久化索引。内联搜索只匹配四个主字段、使用子串匹配，
  避免“短关键词命中长路径 / 模糊子序列”带来的意外结果；需要按字段或跨字段查找时使用高级搜索对话框。
- **随机播放**：没有采用“纯随机下一首”，避免同一首歌在一轮内重复；没有实现完整播放历史栈，
  “上一首”在随机模式下只回退当前一轮内的前一首，边界处重播当前曲目。

## 影响

- 配置模型新增 `playbackMode` 与 `hotkeys` 两个字段；旧 `settings.json` 缺少字段时由默认值 / 归一化补齐，向后兼容。
- 全局快捷键需要程序保持运行；程序退出后系统注册自动注销。
- 搜索仅作用于当前来源（播放列表 / 媒体库）已加载的曲目，不跨播放列表搜索；高级搜索条件不持久化，
  关闭应用后恢复为无过滤状态。
- `HotkeyAction` 新增 `OpenSearch`；旧配置不受影响（按名称反序列化，未知值由归一化丢弃）。
- 列宽修复不改变数据格式，只改变保存时机；分组视图关闭时不再用平铺表格覆盖宽度。

## 验证

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
