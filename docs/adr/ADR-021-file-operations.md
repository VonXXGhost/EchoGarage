# ADR-021：歌曲右键菜单「文件操作」（复制到 / 移动到 / 删除到回收站）

- 状态：**Accepted**（2026-11-15，owner；2026-11-16 并入原 ADR-021-A1）
- 日期：2026-11-14
- 实现：2026-11-14 提交（`052a353` 菜单 / 操作服务 / 库同步，`67cc05a` 移动前二次确认）；2026-11-16 删除后播放列表清理（原 ADR-021-A1，已并入本文）
- 相关：ADR-003（文件是唯一真相）、ADR-010（元数据缓存失效）、ADR-018（播放列表外部文件标签刷新）、ADR-020（播放中文件写入）

## 背景

目前从歌曲列表里操作文件只能「在资源管理器中显示」，要复制 / 移动 / 删除还得手动去
资源管理器操作，然后回到程序里重新扫描。owner 要求在歌曲右键菜单里增加「文件操作」，
菜单形态参考资源管理器的级联菜单：

```
文件操作 ▶ ┌ 复制 到 ▶ ┌ <记忆的目标文件夹 1>
           │            ├ <记忆的目标文件夹 2>
           │            └ 选择文件夹…
           ├ 移动 到 ▶  （同上）
           ├ 重命名 到…（本 ADR 暂不实现）
           ├ ─────────────
           └ 删除文件
```

要求：

1. 复制 / 移动的目标目录可在设置里配置并记忆，右键菜单按记忆顺序列出；
2. 删除文件走回收站（可还原），不做永久删除；
3. 「重命名 到…」本次忽略（菜单项置灰占位）。

## 决策

### 1. 分层职责

- `EchoGarage.Core.Services.FileDestinations`：目标目录列表的纯数据整理
  （去空白 / 去「复制为路径」带上的成对引号、限绝对路径、忽略大小写去重、保留顺序、上限 20 个）。
- `EchoGarage.Core.Services.FileOperationPlanner`：目标路径规划——同目录判定与重名规避
  （`song.flac` → `song (2).flac` → `song (3).flac`…，最多 999 次），**绝不覆盖**已有文件。
- `EchoGarage.Core.Abstractions.IFileOperationService`：跨层接口，返回逐条结果
  （`FileOperationItem(SourcePath, TargetPath, Error)` + 汇总 `FileOperationResult`），
  失败不抛异常而是逐条记录，UI 可以聚合提示「3 个成功，1 个失败」。
- `EchoGarage.Infrastructure.Files.FileOperationService`：后台线程执行复制 / 移动；
  目标目录不存在时创建；跨卷移动退化为「复制 + 删除源文件」。
- `EchoGarage.Infrastructure.Windows.ShellRecycleBin`：通过 `SHFileOperation`
  （`FO_DELETE` + `FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI`）
  把文件送进回收站，避免自己实现回收站元数据。

### 2. 数据库与界面同步

文件被移动 / 复制 / 删除后，程序里的列表与播放列表必须跟着变，因此：

- 移动：调用新增的 `ILibraryRepository.UpdateTrackPathAsync(oldPath, newPath, folderId)`，
  在**一个事务**里更新 `tracks.Path / FolderId / IsInLibrary / IsMissing`
  以及 `playlist_items.TrackPath`（两处都必须改，播放列表按路径关联）；
  若目标路径已存在一行旧缓存，先删除再改，避免撞 `Path` 唯一约束。
- 复制到媒体库内：用 `ILibraryScanner.IndexFileAsync` 让新文件进入库；复制到库外则不索引。
- 删除：`MarkTracksMissingAsync` 把行标记为缺失（媒体库 / 文件夹树 / 智能播放列表据此排除；保留该行以便从回收站还原后重新入库）；同时把对应条目从**所有普通播放列表**移除（`IPlaylistRepository.RemovePlaylistItemsByPathAsync`，见 §3）。
- 以上操作完成后统一 `ReloadSourcesAsync` / `RefreshCurrentSourceAsync(preserveSelection: true)`
  （界面绑定的是当前选中列表，不能无脑 Reload，必须刷新「当前来源」），
  并失效对应路径的波形缓存 `IWaveformService.Invalidate`。
- 若被操作的文件正在播放（`IAudioPlayer.CurrentTrackPath`），先停止播放再改库并提示。

### 3. 删除到回收站后的播放列表清理（2026-11-16，并入原 ADR-021-A1）

owner 需求：右键「删除文件」（移到回收站）成功后，这些歌曲应自动从**所有普通播放列表**中移除，而不是只在媒体库里标记为缺失、让播放列表留下永远无法播放的行。

原实现只调用 `MarkTracksMissingAsync`，`playlist_items` 行原样保留；普通播放列表查询用 `LEFT JOIN tracks`，于是该行显示为“缺失”，用户仍需逐个列表手动删除。智能播放列表没有 `playlist_items` 行，按 `t.IsInLibrary = 1 AND t.IsMissing = 0` 查询，标记缺失后自然消失。`playlist_items.TrackPath` 建表时为 `TEXT NOT NULL COLLATE NOCASE`（`SqliteDatabase` v1），而 `UpdateTrackPathAsync` 已在同一事务里同时更新 `tracks.Path` 与 `playlist_items.TrackPath`——说明播放列表与歌曲的关联键就是规范化路径。

语义：**删除到回收站 = 用户确认过的删除**。相应地，从回收站还原文件只恢复文件与媒体库，不再自动恢复播放列表成员关系。

#### 3.1 接口与方法签名

`EchoGarage.Core.Abstractions.IPlaylistRepository` 新增：

```csharp
Task<int> RemovePlaylistItemsByPathAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
```

与现有 `RemovePlaylistItemsAsync(IReadOnlyList<long> playlistItemIds)` 成对：后者按播放列表条目 Id 精确删除（界面里逐项移除），本方法按歌曲路径批量删除（文件删除后跨所有播放列表清理）；`ByPath` 指明关联键，返回 `int` 为实际删除行数，供状态栏 / INFO 日志使用。`SqlitePlaylistRepository` 实现：

1. 空列表直接返回 0，**不打开数据库连接**；
2. 每个路径经 `PathUtil.TryNormalize` 规范化（完整路径、统一 Windows 分隔符、去引号），单个坏路径跳过而不是抛异常，避免一条脏数据让整批清理失败；
3. 规范化后按 `OrdinalIgnoreCase` 去重（同一路径大小写变体只查一次）；
4. 在**一个事务**内执行参数化 `IN` 查询：`DELETE FROM playlist_items WHERE TrackPath COLLATE NOCASE IN @paths;`
5. 返回影响行数；大于 0 时记 Debug 日志。

显式写出 `COLLATE NOCASE` 让意图不依赖建表语句；分隔符 / 相对路径由 `PathUtil.TryNormalize` 统一，与 `UpdateTrackPathAsync` 使用同一套规范化。注意 SQLite 的 `NOCASE` 只折叠 ASCII，非 ASCII 字母的大小写差异不会命中（见“未做 / 后续”）。

#### 3.2 App 层调用与失败降级

`MainViewModel.DeleteSelectedFilesAsync` 在原有流程上插入一步（不改其它文件操作）：

1. 回收站删除成功项 `deleted`（原有逻辑）；
2. `MarkTracksMissingAsync(deleted)`（原有逻辑保留：媒体库与智能列表口径仍是“缺失”）；
3. 紧接着调用 `RemovePlaylistItemsByPathAsync(deleted)`，用 try/catch 包住：失败只写 `[WARN] Removing deleted files from playlists failed: ...`，**不弹错误框、不改变“已删除文件”结果**；然后继续原有的波形失效 / `StopPlaybackIfAffected` / `ReloadSourcesAsync` / `RefreshCurrentSourceAsync(preserveSelection: true)`；
4. 全部成功且删除行数 M > 0 时，状态栏显示 `已把 N 个文件移到回收站，并从播放列表移除 M 项`，INFO 日志同时记录两个数；M = 0 时保持原文案 `已把 N 个文件移到回收站`；
5. 部分失败分支（回收站逐条失败）、整体异常分支、无选中 / 取消等原有行为不变。

为什么“标记缺失”和“删播放列表条目”都保留：`tracks` 行仍可能被文件夹树、媒体库与智能播放列表规则引用，标记缺失让这些入口立即排除该歌曲；普通播放列表按条目保存成员关系，所以必须删条目。两者语义不同、都需要。刷新一致性：`ReloadSourcesAsync` 里的 `GetPlaylistCountsAsync` 会对 `playlist_items` 重新 COUNT；当前来源是播放列表时 `RefreshCurrentSourceAsync(preserveSelection: true)` 会重新加载行，界面即时反映移除结果。

#### 3.3 与外接扫描 / 文件监听的关系

- 删除文件时，文件监听（`LibraryFileWatcher`）也可能收到删除事件，`ProcessWatchedFileChanges` 同样只走 `MarkTracksMissingAsync`，不碰播放列表；即使与本次删除并发，本方法也幂等（再删一次 0 行）。
- 下次全库扫描（`LibraryScanner.ScanFolderAsync`）对消失文件同样只标记缺失，不会自动清播放列表项。**本决策只覆盖“在本程序内删除到回收站”这一入口**；在资源管理器里直接删除、或外接盘未挂载导致的“消失”，仍保留播放列表缺失条目——外部删除无法区分“移动 / 临时离线 / 真删除”。
- 文件被用户从回收站还原到原路径后：扫描会重新入库（`IsMissing = 0`），但播放列表条目已删除，不会自动回到播放列表；这是“用户确认删除”语义的直接后果。
- 没有 schema 变更，也没有新增“每次启动都执行”的同步 IO。

#### 3.4 失败降级与并发

- DB 忙 / 锁 / I/O 异常：WARN 降级——文件已进回收站、曲目行已标记缺失；播放列表条目保留，表现为原来的“缺失条目”，用户仍可手动移除；下一次删除操作不会自动补偿这些残留。
- 事务：单条 DELETE 本身原子；显式事务保证与将来扩展（多语句）一致，忙碌失败时不会留下半清理状态。
- 并发 / 重复删除：同一路径删两次第二次返回 0；路径大小写不同也命中同一条目；只按 `TrackPath` 精确删除，不会误删其它列表项，也不会误删智能播放列表（其没有行）。
- 未新增索引：`playlist_items` 现只有 `IX_playlist_items_Playlist(PlaylistId, Position)`，按 TrackPath 删除是全表扫描；该表规模是所有普通播放列表条目总数（通常几百到几千行），单次 DELETE 为毫秒级。若将来列表条目规模上百万，再补 `TrackPath` 索引。

### 4. 双击 / 确认与错误处理

- 删除前弹确认框：单个文件显示文件名，多个显示数量，并注明「可以随时从回收站还原」。
- **移动前同样弹确认框**（owner 要求）：显示目标目录与「原位置的文件会被移除」；
  单个文件显示文件名，多个显示数量。复制不弹确认（不改变原文件）。
- 复制 / 移动的目标目录如果来自「选择文件夹…」，该目录自动加入记忆列表。
- 失败项聚合为一个错误对话框（最多列出前若干条），同时写 `[WARN]` 日志；不静默失败。

### 5. 设置与菜单

- `AppSettings.FileDestinations: List<string>`，加载时经 `FileDestinations.Normalize` 归一化；
  设置页新增「文件操作目标文件夹」区块（列表 + 添加 / 移除 / 上移 / 下移，最多 20 个）。
- 右键菜单子项在列表控件的 `ContextMenuOpening`（菜单显示之前）一次性构建
  （WPF 中 `MenuItem` 没有子项就不会弹出二级菜单）：
  二级 `复制 到 / 移动 到` 各自再填充三级目标目录；末尾固定追加
  `选择文件夹…`（临时目标，不入库）与 `管理目标文件夹…`（打开设置页）。
  不在 `ContextMenu.Opened` / `MenuItem.SubmenuOpened` 里改子项——那会在菜单已显示后重建
  可视化容器，鼠标移入二级菜单时整条菜单链会被收起（已在实机反馈中确认并修复）。
- 多选时作用于所有选中行，未多选时作用于当前行；没有选中项时整个「文件操作」菜单禁用。

## 备选方案

1. **调用 `IFileOperation`（Windows Shell COM）做进度与撤销**：体验最好，但需要 COM 互操作与
   更长的实现周期，且与当前「简单聚合结果」的 UI 风格不匹配；留作后续优化。
2. **直接 `File.Delete`**：不符合 owner「删除到回收站」的要求。
3. **用资源管理器打开并让用户手动操作**：现有「在资源管理器中显示」已能做，但用户仍需手动同步库，
   不满足需求。
4. **维持现状（只标记缺失）**：播放列表残留永久缺失行，owner 已明确不接受；否决。
5. **软删除**（`playlist_items` 增加 `RemovedAtUtc` / `IsDeleted` 列）：可支持“从回收站还原时恢复播放列表成员
   关系”，但需要 schema v5 迁移（v4 已被缓存修复迁移占用，见 ADR-014）、所有播放列表查询
   加过滤条件，而目前程序没有“从回收站还原”入口，收益不足；暂缓，若未来做撤销删除再考虑。
6. **只删当前正在显示的播放列表**：同一首歌在多个列表时仍要逐个清理，不符合“所有普通播放列表”的要求；否决。
7. **后台队列 / 延迟批量清理**：一次删除通常是个位数文件、只是一条 SQL，同步等待只有几毫秒，不值得引入队列
   与额外失败状态；否决。

## 回滚

- 代码回滚：删掉 `DeleteSelectedFilesAsync` 中对 `RemovePlaylistItemsByPathAsync` 的调用（接口与实现可保留），
  行为即回到原文“仅标记缺失”。
- 数据回滚：`playlist_items` 行被删除后没有历史记录，无法自动恢复；只能手动重新加入播放列表，或从删除前的
  `library.db` 备份恢复。若今后需要“撤销删除”，应走软删除方案。
- 文件回滚：文件系统操作未变（仍走回收站），用户随时可还原文件；只是播放列表成员关系不会随之恢复。

## 影响

- 新增 Core 3 个文件、Infrastructure 2 个文件、1 个接口文件；`AppSettings` / `JsonSettingsService` 各加 1 处；
  `ILibraryRepository` 与 `SqliteLibraryRepository` 各加 1 个方法。
- 移动 / 删除会改动用户的真实文件，因此所有操作都有确认（删除）或明确的目标目录选择（移动），
  不提供「静默批量转移」入口。
- 播放中删除 / 移动文件在共享句柄（ADR-020）下通常能成功，但当前实现选择先停止播放，避免边播边删的歧义。
- 2026-11-16 并入的修订（原 ADR-021-A1）：`IPlaylistRepository` 与 `SqlitePlaylistRepository` 各 +1 方法
  （`RemovePlaylistItemsByPathAsync`），`MainViewModel.DeleteSelectedFilesAsync` 内联清理与失败降级，
  `RepositoryTests` 新增 3 个测试；不新增依赖、不改 schema。

## 测试

- `FileDestinations.Normalize/Add/Remove`：去引号、去重（忽略大小写）、保持顺序、上限与相对路径拒绝。
- `FileOperationPlanner`：同目录判定、重名递增后缀、不覆盖。
- `FileOperationService`：复制到不存在目录、同名不覆盖、移动删除源文件、同目录拒绝、源文件缺失逐条报错、
  删除走回收站接口（用 FakeRecycleBin 验证成功 / 拒绝两条路径）。
- `SqliteLibraryRepository.UpdateTrackPathAsync`：同时更新 `tracks` 与 `playlist_items`；
  目标路径已有旧缓存行时不撞唯一约束。
- `RepositoryTests.RemovePlaylistItemsByPath_RemovesFromEveryNormalPlaylist_AndKeepsOtherItems`：
  同一路径出现在两个普通播放列表 → 两处条目都移除、其它歌曲条目保留、返回行数 = 2、两个列表计数各为 1。
- `RepositoryTests.RemovePlaylistItemsByPath_UnknownPathOrEmptyList_ReturnsZero`：
  未知路径返回 0 且不动已有条目；空列表返回 0。
- `RepositoryTests.RemovePlaylistItemsByPath_MatchesDifferentPathCaseAndSeparators`：
  全大写路径、正斜杠路径均能命中同一条目（验证 `COLLATE NOCASE` 与路径规范化）。
- 原有 `UpdateTrackPath_MovesTrackRowAndPlaylistItems` / `UpdateTrackPath_DeletesStaleRowAtNewPath` 不回归。

## 未做 / 后续

- 「重命名 到…」菜单项保留置灰，未实现。
- 复制 / 移动的大批量进度条与取消：当前逐文件串行、无进度 UI（数量通常是个位数）。
- 跨卷移动为大文件时是「复制 + 删除」，没有断点续传；失败时源文件保留。
- 列表行迷你波形（ADR-015 §15.2）与本 ADR 无关，未改动。
- 删除确认框文案目前仍写“可以随时从回收站还原”；还原后播放列表不会恢复，是否在文案里补充说明待 owner 决定。
- 外部删除（资源管理器删除、移动磁盘离线被扫描判定消失）不清理播放列表条目，保持现状。
- SQLite `NOCASE` 只折叠 ASCII：非 ASCII 字母大小写差异的路径不会命中（Windows 文件系统可能不区分）；
  实际媒体库路径以 ASCII 盘符 / 目录为主，风险低，后续如需完全对齐可改为取回候选路径在 .NET 侧用
  `OrdinalIgnoreCase` 过滤。
- 撤销删除 / 软删除播放列表条目（还原时恢复成员关系）未做；删除条目后未更新 `playlists.UpdatedAtUtc`，
  与现有 `RemovePlaylistItemsAsync` / `ClearPlaylistAsync` 一致，若后续做播放列表增量同步再统一处理。

## 修订记录

| 日期 | 原文件 | 主要变更 |
| --- | --- | --- |
| 2026-11-14 | ADR-021（本文） | 批准并实现「文件操作」菜单 / 操作服务 / 库同步（`052a353`、`67cc05a`） |
| 2026-11-15 | ADR-021（本文） | owner 批准归档 |
| 2026-11-16 | 原 ADR-021-A1（已并入本文） | 删除到回收站后从所有普通播放列表移除条目：新增 `RemovePlaylistItemsByPathAsync`，`DeleteSelectedFilesAsync` 清理与 WARN 降级，明确“外部删除不清理、还原不恢复条目”边界（§2 / §3） |

> 原 ADR-021-A1 的正文已并入本文，对应文件已从 `docs/adr/` 移除，编号作为追溯信息保留。
