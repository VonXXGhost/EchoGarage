# KB-004：性能基线与列表切换优化

- 状态：Accepted（KB-004 2026-11-09 批准；KB-016 2026-11-16 批准；
  2026-11-19 合并原 KB-016，**原 KB-016 已并入本文**）
- 日期：2026-11-09（启动 / 扫描基线）、2026-11-16（列表切换性能）、2026-11-19（合并）
- 环境：Windows 11（26200）、.NET 10、Debug 构建带日志（保守值）；启动基线为 4 首测试曲目，
  列表切换基线为 1,000 / 5,000 行播放列表
- 关联：`docs/adr/ADR-004-playlist-model.md`、`docs/adr/ADR-013-secondary-row-info-and-composer.md`、
  `docs/adr/ADR-003-files-are-truth.md`、`docs/adr/ADR-010-metadata-cache-invalidation.md`；
  封面与分组列表细节见 KB-008，解码与跳转行为见 KB-001

## 1. 启动 / 扫描 / 外部打开基线

| 指标 | 数值 | 说明 |
| --- | --- | --- |
| 进程启动 → 主窗口显示 | **~0.9 s** | 日志 `Startup complete in ... ms (window shown at 949 ms)` |
| Host 初始化（设置 + SQLite 建表） | ~170–200 ms | 数据库已存在时 |
| 来源列表加载（文件夹 / 播放列表 / 计数） | ~55 ms | 4 首库 + 3 播放列表 |
| 初始播放列表加载 | ~20 ms | 4 行 |
| 后台预热（MF + WASAPI + SQLite） | ~1.0 s | 与 UI 并行，不阻塞 |
| 首次扫描 4 个文件 | 0.2 s | 含标签读取与封面键计算 |
| 重复扫描（无变化） | ~0.0 s | 只比对 stat |
| 外部打开单文件（索引 + 替换默认列表 + 切源 + 起播） | ~1.1–2.4 s | 取决于是否首次初始化音频；第二次约 1.1 s |

补充实测（2026-11-09）：

- 跳转**不重建输出链路**（`Pause → reader.CurrentTime = target → Play`，见 KB-001 §3），
  实测 `Seek()` 调用 2–9 ms、`Seeked to 00:06.000` 后立即继续播放；重建输出才会花约 1 s。
- 单实例转发：第二个进程启动后立即退出，主实例约 1.1 s 内完成“索引 → 覆盖默认列表 → 播放”。

## 2. 已实现的关键优化与依据

1. **不在启动路径枚举音频设备**：设备枚举 + `MixFormat` 第一次约 1.5 s（COM / MF 初始化）。
   改为窗口显示后 `DispatcherPriority.Background` 执行；启动时间从 2.1 s 降到 0.9 s。
2. **后台预热**：窗口显示后 `Task.Run` 调用 `MediaFoundationApi.Startup()`、默认设备 `MixFormat`、
   打开一次 DB 连接。代价约 1 s 后台 CPU，换来首次播放 / 首次切设备的低延迟（ADR-009）。
3. **扫描增量**：以 `FileSize + LastWriteTimeUtc` 判断，未变化不读标签；批量 `UpsertTracks`
   使用单事务 + 200 条分批（ADR-003 / ADR-010）。
4. **封面按专辑缓存 + 行封面按需加载**：同专辑只解析一次；行封面在行可见时加载
   （显示 24×24 DIP、解码宽 128px，见 KB-008）；正在播放面板封面解码 420px；内存 LRU 上限 300 条，
   虚拟化回收时把行的 `CoverImage` 置空。
5. **数据库**：WAL + `synchronous=NORMAL`；常用查询有 `(IsInLibrary, IsMissing)`、`FolderId`、
   `(PlaylistId, Position)` 索引。

## 3. 列表切换性能：行单元 / 次要文本延迟物化 + 批量 Reset

### 3.1 问题

列表 / 播放列表切换在媒体库达到数千行时出现明显停顿（窗口只显示几十行），热点三处：

1. **`ApplyTracks` 逐行写入**：`Tracks.Clear()` + 每行 `Tracks.Add(row)`，共 `N+1` 次
   `CollectionChanged`；`Tracks` 同时被平铺 `TracksView` 与分组 `GroupedTracksView` 两个
   `ListCollectionView` 监听，每次通知都触发排序 / 过滤 / 容器生成，成本随行数非线性放大。
2. **`RebuildGroupedView` 为全部行急切创建单元**：对 `Tracks` 里每一行（含从未显示、
   被虚拟化回收的行）创建 `Cells` 与 `RightCells`，每行每列一个 `TrackCellViewModel`
   （`N × 可见列数` 个对象）；布局写入路径还常连续触发两次重建（`CurrentLayout` 替换 →
   `OnCurrentLayoutChanged` 一次，随后 `ApplySortAndGrouping` 又一次），造成双倍分配。
3. **`ApplySecondaryTexts` 急切计算全部行**：对所有行执行 `BuildTokenValues()`（每行约 20 个键的
   字典）并对每个可见列做格式模板展开，即使该行从未显示。

目标：把 `O(N × 列数)` 的行内对象分配与文本格式化降为 `O(实际显示行数 × 列数)`，
把 `N+1` 次集合通知合并为 1 次，同时保持排序 / 分组 / 过滤 / 选中恢复 / 正在播放标记 /
封面按需加载 / 表头填充等既有行为。

### 3.2 设计

**`BulkObservableCollection<T>.ReplaceAll`**（`src/EchoGarage.App/Services/BulkObservableCollection.cs`）

- `sealed class BulkObservableCollection<T> : ObservableCollection<T>`；
- `ReplaceAll(IEnumerable<T> items)`：先物化输入（`items as IList<T> ?? items.ToList()`，处理“传入自己”）；
  `CheckReentrancy()` 后直接改 `Items`（`Clear` + `Add`，不发中间通知）；最后只发一次
  `PropertyChanged(Count)` + `PropertyChanged("Item[]")` + `CollectionChanged(Reset)`。
- `Tracks` 属性类型改为 `BulkObservableCollection<TrackRowViewModel>`，仍绑定同一实例、不替换引用；
  `ApplyTracks` 先在局部 `List<TrackRowViewModel>` 中建全部行（保留正在播放识别、`CurrentRow`、
  `UpdateNowPlayingMetadata`、`BeginNowPlayingCoverLoad`），再 `Tracks.ReplaceAll(rows)`；
  `_tracksVersion++` 使分组重建签名必然变化。

**行单元延迟物化**（`TrackRowViewModel`）

- `Cells` / `RightCells` 改为只读 getter：首次绑定访问时按当前 `GroupedCellLayout` 构建并缓存
  （`_cells ??= _cellLayout.BuildCells(this, right: false)`）；未显示的行永不分配。
- `SetCellLayout(GroupedCellLayout layout)`：布局引用相同直接返回；只对**曾经被绑定过**的行
  （`_cellsObserved` / `_rightCellsObserved`）清缓存并发 `OnPropertyChanged(Cells / RightCells)`，
  从未显示的行只换引用、不通知不分配。
- `bool HasResolvedCells`：供日志统计“实际物化行数”。
- `internal sealed class GroupedCellLayout`（与行同文件）：不可变快照（左右固定块表头数组 +
  `DataTemplate` 数组，`static Empty`），`BuildCells(row, right)` 只在调用时创建数组；
  无列时返回共享 `Array.Empty<TrackCellViewModel>()`。

**次要文本延迟计算**

- `SecondaryTexts` 改 getter 缓存（`_secondaryTexts ??= BuildSecondaryTexts()`；空内容返回共享静态空字典）；
- `ApplySecondaryFormats(formats)`：格式表引用相同则短路；否则记录引用、清缓存，
  只对已观察行发 `OnPropertyChanged(SecondaryTexts)`；
- `UpdateTrack(track)`：清文本缓存后发空属性名通知，让绑定重读并按需重建；
- `MainViewModel.ResolveSecondaryFormats()`：按当前布局生成格式表，算 `key\u001f format` 签名，
  签名未变时复用同一字典实例，使“无关的排序 / 布局刷新”不再让所有行失效。

**分组重建去重（失效签名）**：`RebuildGroupedView()` 开头计算
`BuildGroupedRebuildSignature(grouped)`：

```
_tracksVersion | G/F | ShowSecondaryInfo | SortColumn | SortDescending | GroupSortByDiscTrack
| 每列: Key : 可见 : Pin : Width(不变文化) : SecondaryFormat
```

签名相同直接返回。覆盖：行集合版本、平铺/分组模式、排序字段与方向、组内碟片-曲目排序、
每列可见性 / 左右固定 / 宽度 / 次要格式。同时消除两类重复重建（布局替换 + `ApplySortAndGrouping`
连续调用、列宽拖动 / pin 时 `PersistCurrentLayout` 与显式重建叠加）。仅真正重建时才触发
`GroupedColumnsRebuilt`。

**轻量耗时日志**

- `ApplyTracks` 每次切换一条 Info：
  `List switch: <Kind> '<Name>' rows=N, buildRows=…ms, replaceAll=…ms, sortGroup=…ms, filter=…ms, total=…ms`
- `RebuildGroupedView` 每次真正重建一条 Debug：
  `RebuildGroupedView: grouped rows=N, cols=L+R, boundRows=…, secondary=…ms, elapsed=…ms`
  （平铺为 `RebuildGroupedView: flat rows=N, secondary=…ms, elapsed=…ms`）。
- 切歌 / 标签更新等高频路径不新增日志；每条日志均为 O(1) 字符串拼接。

### 3.3 失效策略矩阵

| 变化 | 失效动作 | 实际代价 |
| --- | --- | --- |
| 切换来源 / 播放列表（行集合变化） | `_tracksVersion++` → 签名变化 → 重建；新行默认未物化 | O(N) 建行 + 1 次 Reset；单元在显示时创建 |
| 列增删 / 可见性 / 顺序 / 宽度 / 左右固定 | `CurrentLayout` 替换 → 签名变化 → 重建表头与布局引用 | 已绑定行各发一次 PropertyChanged；未绑定行仅换引用 |
| 次要信息开关 / 次要格式模板变化 | 格式表签名变化 → 新字典；重建签名同时变化 | 已显示行重算一次；未显示行不计算 |
| 仅排序 / 分组切换 | 签名变化（排序字段、模式） | 重建表头排序箭头；行内容不重建 |
| 重复调用（布局写两次） | 签名相同 → 直接返回 | O(列数) 字符串比较 |
| 行内标签更新（`UpdateTrack`） | 清文本缓存 + 空名通知，不改单元 | 仅该行的可见绑定重读 |
| 仅封面大小变化（`PersistGroupCoverSize`） | 签名不含封面尺寸 → 不重建分组行 | 封面栏由窗口自身绑定刷新 |

### 3.4 预期收益与度量

1. `N+1` 次集合通知 → 1 次 `Reset`（两个 `ListCollectionView` 的排序 / 过滤 / 容器生成只跑一次）；
2. `N × (左列数 + 右列数)` 个 `TrackCellViewModel` → `可见行数 × 列数`（通常 30–60 行），
   布局未变时为零；
3. `ApplySecondaryTexts` 的逐行 `BuildTokenValues()` 与模板格式化 → 仅已显示行；
4. 布局 / pin / 宽度拖动路径的重复重建 → 签名去重后只重建一次。

度量：对比 `List switch:` 日志的 `buildRows` / `replaceAll` / `sortGroup` / `filter` / `total` 分段
（同一来源、同一行数，如 `Demo Playlist A` 1,000 / 5,000 行两档）；看 `RebuildGroupedView:` 的
`boundRows` 与 `rows` 比例确认未显示行未物化；用 `dotnet-counters` 等对比切换时分配量；
观察切换后首次滚动是否可感知掉帧。

### 3.5 风险与缓解

| 风险 | 说明 | 缓解 |
| --- | --- | --- |
| 滚动首帧成本 | 单元 / 次要文本从“切换时一次算完”变成“每行首次显示时算”，快速滚动可能更抖 | 每行对象很小（数组 + 若干 `TrackCellViewModel`），纯内存无 IO；先测再决定是否预热可见区 |
| 缓存内存 | 已绑定行缓存单元与文本，滚动过的行保留到列表切换为止 | 引用型小对象（`N × 列数 × 几十 B`）；切换播放列表即整体丢弃；后续可在容器回收时释放（需了解容器生命周期，本轮不做） |
| 虚拟化回收 | 容器回收后行对象仍在 `Tracks` 中，其缓存不自动释放；再次显示直接复用 | 复用即收益；`SetCellLayout` 只对已观察行发通知 |
| 表头填充 | 布局未变时不触发 `GroupedColumnsRebuilt`，`UpdateGroupedHeaderFill` 不执行 | 窗口在尺寸变化时自行重算（既有路径），签名未变说明列宽 / 结构一致 |
| 绑定行为 | `Cells` / `RightCells` / `SecondaryTexts` 由可 set 属性改为只读 | XAML 只读取绑定；仓库内无代码赋值（旧 `RebuildGroupedView` 赋值已迁移）；`ContentControl` 绑定语义不变 |
| 选中 / 播放行 | 重建行对象后 `IsPlaying`、`CurrentRow`、多选恢复 | 相关逻辑保持原顺序；`ReplaceAll` 后 `ApplyFilter` / 多选恢复流程不变 |
| 回滚 | 改动集中在 4 个文件 | 见 §6；无 schema / 设置格式变化，回滚不需数据迁移 |

## 4. 已知的慢点

- **首次 MP3 播放**：旧结论（2026-11-09）是 NAudio `Mp3FileReader` 走 ACM/DMO 初始化、约 1 s。
  **现已统一走 Media Foundation**（`SharedAudioFileReader` 对非 wav/aiff 全部使用
  `StreamMediaFoundationReader`，且 MF 已随启动预热，见 KB-001 §1 / §2），该慢点不再适用；
  保留此条仅作历史记录。
- **大量文件（数万）首次导入**：受磁盘随机读与标签解析限制；扫描并发限制在 `min(CPU, 8)`，
  避免打满磁盘。
- **分组渲染开销**：分组容器是独立 `ListView`（`GroupedList`，见 KB-008 §1），1 万行下分组仍有
  可见开销；关闭“按专辑分组”可换取更快的滚动。旧文档写的“DataGrid 分组”已过时。

## 5. 测量方法

日志中检索：

```
Host initialized in ... ms
Sources loaded in ... ms
Initial playlist loaded in ... ms total
Startup complete in ... ms (window shown at ... ms)
Warmup finished in ... ms
External open phases: index=..., playlist=..., sources=..., select=..., play=... ms
List switch: <Kind> '<Name>' rows=N, buildRows=...ms, replaceAll=...ms, sortGroup=...ms, filter=...ms, total=...ms
RebuildGroupedView: grouped rows=N, cols=L+R, boundRows=..., secondary=...ms, elapsed=...ms
```

## 6. 手工验收（列表切换，2026-11-16）

环境：`EchoGarageData` 使用测试副本（含 1,000+ 行媒体库，曲目用 dummy 值），不改动
`D:\Temp\EchoGarage-UAT`。

1. 切换到媒体库，再依次切换 2–3 个播放列表与文件夹：不卡顿；标题 / 副标题 / 行号 /
   正在播放标记正确；日志出现带分段耗时的 `List switch:`。
2. 平铺视图 ↔「按专辑分组」：表头列宽、排序箭头、左右固定块位置正确，表头右侧填充无偏移。
3. 拖动分组表头列宽 / 切换左右固定 / 编辑列布局 / 应用预设：行跟随变宽变窄，不出现空白单元或
   错位；同一操作日志中的重建次数不再重复翻倍。
4. 打开「次要信息」并给若干列设置次要格式（`Demo Album A` / `Demo Track 01` 字段模板）：
   可见行显示两行文本，关闭后恢复单行；修改模板可见行立即更新，未显示行滚动到时也正确。
5. 播放 `Demo Track 01` 后切换播放列表再切回：▶ 标记、播放面板、封面按需加载正确；
   多选 / 选中行恢复与改动前一致。
6. 搜索 / 智能过滤后行数、副标题、单元格正确；清除过滤后恢复；顶部 / 底部快速来回切换 10 次，
   无异常日志、无花屏，内存一次 GC 后回落。

## 7. 代码位置与回滚点

- `src/EchoGarage.App/ViewModels/MainViewModel.cs`：`ApplyTracks`（批量构建 + `ReplaceAll` + Info 日志）、
  `RebuildGroupedView`（签名去重 + 共享 `GroupedCellLayout` + Debug 日志）、
  `ResolveSecondaryFormats` / `ApplySecondaryTexts`（共享格式表），字段 `_tracksVersion` /
  `_groupedRebuildSignature` / `_secondaryFormats` / `_secondaryFormatsSignature`、`Tracks` 属性类型。
- `src/EchoGarage.App/ViewModels/TrackRowViewModel.cs`：`Cells` / `RightCells` / `SecondaryTexts`
  延迟 getter、`SetCellLayout`、`HasResolvedCells` / `HasResolvedSecondaryTexts`、`GroupedCellLayout`。
- 新增 `src/EchoGarage.App/Services/BulkObservableCollection.cs`。
- 无 schema、设置、主题令牌、XAML 模板改动。

回滚点（按文件独立回滚，无数据迁移）：

1. `ApplyTracks` 恢复为 `Tracks.Clear()` + 逐行 `Add`（保留 Info 日志不影响功能）；
2. `RebuildGroupedView` 恢复为直接给 `row.Cells` / `row.RightCells` 赋值并删除签名去重
   （此时 `Cells` 需恢复 setter）；
3. `TrackRowViewModel` 恢复 `SecondaryTexts` 为 set 属性并立即重算；
4. 删除 `BulkObservableCollection.cs` / `GroupedCellLayout`，`Tracks` 类型改回
   `ObservableCollection<TrackRowViewModel>`。

## 8. 验证记录

- 2026-11-16：Debug / Release 全量构建通过（0 警告 0 错误）；xUnit v3 进程内测试
  `Total: 579, Errors: 0, Failed: 0, Skipped: 0`（当时快照；2026-11-19 ADR-index 记录全库约 795 项）。
- 未运行 UI 自动化（遵守“不抢占前台窗口”约定）；§6 手工验收步骤待 owner 执行。
- 文档中的歌曲 / 专辑 / 艺术家均为 dummy 值（`Demo Artist` / `Demo Album A` / `Demo Track 01` /
  `Demo Playlist A`）。

## 9. 修订记录

- 2026-11-09：KB-004 初版（启动 / 扫描基线、优化依据、慢点、测量方法）。
- 2026-11-16：KB-016 初版（列表切换：`ReplaceAll`、行单元与次要文本延迟物化、重建签名去重、
  失效矩阵、风险、验收与回滚）。
- 2026-11-19：owner 要求简化合并文档：原 KB-016 全文并入本文
  （**原 KB-016 已并入本文**），标题改为「性能基线与列表切换优化」；
  修正事实：行封面 56px → 显示 24×24 DIP / 解码宽 128px；跳转改为“不重建输出链路”；
  “首次 MP3 播放约 1 s”与“DataGrid 分组”标为历史结论并指向 KB-001 / KB-008；
  状态行统一为 Accepted 并补合并说明。
