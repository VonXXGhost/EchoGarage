# KB-009：分组智能判定、组内排序开关、滚动条最小滑块与播放封面加载态

- 状态：**已生效**（2026-11-11 获 owner 批准；同日补充第 6 节排序持久化修复）
- 日期：2026-11-11
- 适用版本：v0.1.0 后续迭代
- 涉及文件：`MainViewModel`、`MainWindow`（分组/平铺视图切换、播放面板）、`ColumnsWindow`、
  `PlaylistViewLayout` / `PlaylistSortPolicy` / `ViewLayoutFactory`、`Controls.xaml` / `MinThumbTrack`
- 与既有文档的关系：KB-008 中“分组/平铺切换由 `GroupByAlbum` 直接控制视图可见性”的描述由本文第 1 条取代；
  KB-008 其余内容（foobar2000 风格封面栏、封面尺寸持久化、进度条/音量）仍然有效。

## 1. 分组只在“专辑 / 专辑艺术家”排序时显示

- 规则：`IsGroupedViewActive = GroupByAlbum && PlaylistSortPolicy.SupportsAlbumGrouping(CurrentLayout.SortColumn)`。
  `SupportsAlbumGrouping` 只接受 `Album` 与 `AlbumArtist` 两个排序属性（`PlaylistSortPolicy`，有单元测试）。
- 其它排序（标题、艺术家、时间、年份、默认的 `RowNumber` 顺序……）一律显示平铺 `DataGrid`，
  **忠实按所选顺序排序，不再把不连续的同一专辑歌曲合并成组**。
- 两个 `ListCollectionView` 仍然共用 `Tracks`：平铺视图永远不分组；分组视图只在有效分组时
  挂 `PropertyGroupDescription(AlbumGroupKey)`，否则仅按同一主排序排列（与平铺视图顺序一致）。
- 播放顺序（`GetPlaybackOrder()`）跟随“当前实际显示”的视图，避免显示顺序与“下一首”不一致。
- “视图 → 按专辑分组”复选框仍然是用户意图开关；排序切到非专辑列时列表自动回到平铺，
  勾选状态下切回专辑/专辑艺术家排序会自动恢复分组。
- 兼容性：旧布局 `sortColumn` 为空（默认顺序）时现在显示平铺视图，不再默认合并分组；
  这是该规则有意带来的行为变化。

## 2. 分组内默认按“碟片号 → 曲目号”升序，可在列与布局中关闭

- `PlaylistViewLayout.GroupSortByDiscTrack`（默认 `true`，JSON 字段 `groupSortByDiscTrack`）：
  - 分组有效时，排序描述为：专辑/专辑艺术家主排序（按用户升降序）→ `AlbumGroupKey`（同方向，
    保证同一专辑严格连续）→ `DiscNumberSort` 升序 → `TrackNumberSort` 升序 → `RowNumber` 升序兜底。
  - 关闭后跳过碟片/曲目两级，组内保持播放列表当前顺序（`RowNumber`）。
  - 专辑主排序为降序时，仅组间顺序反转；组内碟片/曲目始终升序。
- “列与布局”对话框新增复选框“分组内按碟片号 / 曲目号排序”与说明文字；该开关按播放列表保存
  （媒体库布局保存在配置里），应用显示预设和列调整时保留该值（与 `GroupCoverSize` 同样的处理）。
- 旧配置没有该字段时按 `true` 处理（JSON 反序列化默认值），无需迁移。

## 3. 滚动条滑块最小可视高度（MinThumbTrack）

- 需求：列表右侧竖向滚动条在大库下按比例计算出的滑块只有约 6–8px，太难点。
- 实测：只给 `Thumb.MinHeight = 12` 时 `ActualHeight` 变成 12，但 WPF 仍按 Track 算出的
  arrange slot（约 8.5px）给 Thumb 加 layout clip，屏幕上依然只有约 7px——因此不能只改 MinHeight。
- 方案：`MinThumbTrack : Track`（`EchoGarage.App/Services/MinThumbTrack.cs`），在竖向 ScrollBar 模板中
  替代默认 `Track`：
  1. 先 `base.ArrangeOverride`；
  2. `computed = trackLength * ViewportSize / (Maximum - Minimum + ViewportSize)`；
  3. `actual = max(computed, MinThumbLength)`，`fraction = clamp((Value - Minimum) / range)`；
  4. `thumb.Arrange(new Rect(0, fraction * (trackLength - actual), width, actual))`。
- 效果：arrange slot 本身就是最小长度，无 layout clip；滑块按比例移动、滚到底部时底边与轨道末端齐平，
  不会溢出；横向滚动条继续使用默认 `Track`（不受影响）。
- XAML 中 `MinThumbLength="14"`：共享 Thumb 模板的 Border 上下各留 1px 内边距，因此屏幕上实际着色高度
  ≈ 12px，约为此前可视高度（约 6–7px）的两倍。若以后调整 Thumb 模板边距，需要同步调整该值。
- 已知取舍：Track 内部拖拽换算仍使用它自己的比例长度，视觉槽位比内部值长，
  拖动时值与鼠标位移有极轻微偏差（像素级），实测不影响使用；滚动/点击翻页行为不变。

## 4. “正在播放”封面加载动画与切歌立即清除旧封面

- 旧行为：切歌后旧封面一直显示到新封面解码完成，容易误以为封面没换。
- 新行为：
  - `PlayRowAsync` 一开始就调用 `BeginNowPlayingCoverLoad`：若新曲目路径与当前封面路径不同，
    立刻 `CurrentCover = null`；随后异步加载 420px 封面。
  - 版本令牌 `_nowPlayingCoverVersion`：每次切歌/停止自增；慢速解码返回时若版本过期或 `CurrentRow`
    已变化直接丢弃，不会把旧歌封面盖到新歌上；只有最新请求能清除 `IsNowPlayingCoverLoading`。
  - 面板左上角新增 `IsNowPlayingCoverLoading` 对应的旋转弧线（Accent 圆弧 + 淡轨道圆，主题令牌着色），
    使用 `DataTrigger` + Storyboard；淡入延迟 120ms，缓存命中时几乎不闪。
  - 无封面且有当前歌曲时显示 ♪ 占位符（`ShowCoverPlaceholder`）；停止/清空时封面与加载态一起复位。
  - 标签编辑刷新当前曲目时也走同一路径（保留旧图直到新图更替，不显示加载动画）。
- 应用内验证（临时诊断，未提交）：`--play-file A` → 1.8s 后切到 B 并人为延迟封面解码，
  截图确认旧封面已移除、旋转加载动画可见；日志确认版本丢弃逻辑生效。

## 5. 测试与验证

- 新增/扩展单元测试（80 个，79 个通过；文件写入注册表的 1 个测试在受限会话跳过）：
  - `PlaylistSortPolicyTests`：仅 `Album`/`AlbumArtist` 允许分组，其它排序（含 null）不允许。
  - `ViewLayoutTests.GroupSortByDiscTrack_DefaultsToTrueAndRoundTrips`：默认 true、Clone/Serialize 往返保持 false。
  - `ViewLayoutTests.Normalize_KeepsSortPropertyForVisibleSortableColumns`：所有可排序列的排序属性都能随布局保存；
    列隐藏或属性未知时清空排序。另有 `SerializeRoundTrip_PreservesDurationSort` 作为回归测试。
- 手工/截图验证：
  - 2825 首库：默认顺序 → 平铺 DataGrid；按“专辑”排序 → 分组视图、组内碟片/曲目升序；
    按“标题”排序 → 平铺且不再合并不连续专辑。
  - 构造 5 首歌的多碟专辑（DB 顺序打乱）并用播放列表源验证：
    开关开 → 01/02/01/02/03（碟 1 → 碟 2，曲目升序）；开关关 → 播放列表原始顺序。
  - 2825 首滚动条：滑块可视高度约 12px（改前约 6px），末端贴底不溢出（探针 + 应用截图）。
  - 浅色/深色主界面、列与布局对话框截图已更新。

## 6. 追加修正（2026-11-11）：按“时间”等列排序不持久、箭头错误、二次点击无法倒序

- 现象（owner 反馈）：点击“时间”列头后列表按时间升序显示，但表头箭头仍停在“专辑艺术家”，
  再点“时间”不会切换降序。
- 根因 1（排序属性被清空）：`ViewLayoutFactory.Normalize` 校验 `SortColumn`（存的是行属性名，
  如 `DurationSort`）时拿它和**列 key**（`duration`）直接比较。只有属性名恰好在忽略大小写后等于
  列 key 的排序（`Title`/`Artist`/`Album`/`AlbumArtist`/`Genre`/`Codec`/`Comment`）能存活；
  `DurationSort`/`TrackNumberSort`/`YearSort`/`BitrateSort`/… 会在每次 `PersistCurrentLayout`
  时被清成 null。于是第一次点击虽然排了序，但持久化后 `SortColumn` 变 null（或回退），
  箭头无从显示，第二次点击又被当成“新排序”而永远是升序。
- 根因 2（重建列后视觉顺序丢失）：`RebuildColumns` 为了解决 DataGrid 列重建崩溃会先
  `TrackGrid.ItemsSource = null` 再重新绑定。DataGrid 在 ItemsSource 重新绑定时会清空
  `TracksView.SortDescriptions`，因此重启后即使布局里保存了排序，列表视觉上仍是默认顺序。
- 修复：
  1. `ColumnCatalog.ColumnDescriptor` 增加 `SortProperty` 字段，成为“列 key ↔ 行排序属性”的唯一映射
     （`ColumnCatalog.FindBySortProperty`）；`TrackSortMap`、`ColumnsWindow` 全部改为从 catalog 派生，
     `ViewLayoutFactory.Normalize` 用映射校验“排序列是否可见”。
  2. `MainWindow.RebuildColumns` 在重新绑定 ItemsSource 后调用 `MainViewModel.ReapplySort()`
     重新应用当前排序；`SortBy` 现在也会触发 `SortChanged`，分组表头点击切换排序后
     平铺 DataGrid 的箭头同步刷新。
- 验证（应用内临时诊断，未提交）：
  - 预置 `sortColumn=DurationSort` 启动：列表按时间升序、箭头在“时间”列；
  - 从“专辑”分组视图切到“时间”排序：自动回到平铺、按时间升序、箭头在“时间”；
  - 连续两次排序：第二次变为降序，箭头 ▼，`settings.json` 持久化 `DurationSort/true`。
