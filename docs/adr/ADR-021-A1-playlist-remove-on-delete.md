# ADR-021-A1（提案）：删除到回收站后从普通播放列表移除条目

- 状态：**Accepted**（owner 批准：2026-11-16；实现已随本提案提交）
- 日期：2026-11-16
- 关联：ADR-021（歌曲右键菜单「文件操作」，已生效；本文件为其修订）、ADR-003（文件是唯一真相）、
  ADR-018（播放列表外部文件标签刷新）、ADR-010（元数据缓存失效）

## 1. 背景与需求

owner 需求（2026-11-16）：右键「删除文件」（移到回收站）成功后，这些歌曲应自动从**所有普通播放列表**中
移除，而不是只在媒体库里标记为缺失、让播放列表留下永远无法播放的行。

现状（本次实现前调查）：

- 右键菜单在 `MainWindow.xaml.cs` 的 `RebuildFileOperationMenu`，「删除文件」→
  `MainViewModel.DeleteSelectedFilesAsync()`。
- 删除成功后只调用 `ILibraryRepository.MarkTracksMissingAsync`（`UPDATE tracks SET IsMissing = 1`），
  `playlist_items` 行原样保留；普通播放列表查询用 `LEFT JOIN tracks`，于是该行显示为“缺失”，
  用户仍需逐个列表手动删除。
- 智能播放列表没有 `playlist_items` 行，按 `t.IsInLibrary = 1 AND t.IsMissing = 0` 查询，
  标记缺失后自动消失（天然不受影响）。
- `playlist_items.TrackPath` 在建表时声明为 `TEXT NOT NULL COLLATE NOCASE`（`SqliteDatabase` v1）；
  `ILibraryRepository.UpdateTrackPathAsync` 已在同一事务里更新 `tracks.Path` 与 `playlist_items.TrackPath`，
  说明播放列表与歌曲的关联键就是规范化路径。

## 2. ADR-021 的承诺与现状差距

ADR-021 §2「数据库与界面同步」承诺“文件被移动 / 复制 / 删除后，程序里的列表与播放列表必须跟着变”：

- 移动：一个事务同时更新 `tracks.Path` 与 `playlist_items.TrackPath`，承诺已兑现；
- 复制：新文件按是否在媒体库内决定入库 / 外链，承诺已兑现；
- 删除：只写了“`MarkTracksMissingAsync` 把行标记为缺失（保留播放列表条目，可从回收站还原后刷新回来）”。

问题在于：保留条目的理由与同段“播放列表必须跟着变”的目标不一致，而且程序里目前**没有**“从回收站还原”
的入口——删除后普通播放列表里留下的是永久缺失行，只能手动清理。本条修订把删除语义对齐 owner 认知：
**删除到回收站 = 用户确认过的删除**，播放列表条目同步移除。相应地，“从回收站还原文件”只恢复文件与媒体库，
不再自动恢复播放列表成员关系（见 §3.3、§5、§7）。

## 3. 决策

### 3.1 接口与方法签名

`EchoGarage.Core.Abstractions.IPlaylistRepository` 新增：

```csharp
Task<int> RemovePlaylistItemsByPathAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
```

命名理由：与现有 `RemovePlaylistItemsAsync(IReadOnlyList<long> playlistItemIds)` 成对——后者按播放列表条目
Id 精确删除（界面里逐项移除），本方法按歌曲路径批量删除（文件删除后跨所有播放列表清理）；`ByPath` 指明关联键，
返回 `int` 为实际删除行数，供状态栏 / INFO 日志使用。

实现（`SqlitePlaylistRepository`）：

1. 空列表直接返回 0，**不打开数据库连接**；
2. 每个路径经 `PathUtil.TryNormalize` 规范化（完整路径、统一 Windows 分隔符、去引号），单个坏路径跳过而不是抛异常，
   避免一条脏数据让整批清理失败；
3. 规范化后按 `OrdinalIgnoreCase` 去重（同一路径大小写变体只查一次）；
4. 在**一个事务**内执行参数化 `IN` 查询：
   `DELETE FROM playlist_items WHERE TrackPath COLLATE NOCASE IN @paths;`
5. 返回 `ExecuteAsync` 的影响行数；大于 0 时记 Debug 日志。

大小写 / 分隔符：`playlist_items.TrackPath` 建表时已带 `COLLATE NOCASE`，此处再显式写出 `COLLATE NOCASE`
让意图不依赖建表语句，与 Windows 路径大小写不敏感的语义一致；分隔符/相对路径由 `PathUtil.TryNormalize` 统一，
与 `UpdateTrackPathAsync` 使用同一套规范化。注意 SQLite 的 `NOCASE` 只折叠 ASCII，非 ASCII 字母的大小写差异
不会命中（见 §7）。

### 3.2 App 层调用与失败降级

`MainViewModel.DeleteSelectedFilesAsync` 在原有流程上插入一步（不改其它文件操作）：

1. 回收站删除成功项 `deleted`（原有逻辑）；
2. `MarkTracksMissingAsync(deleted)`（原有逻辑保留：媒体库与智能列表口径仍是“缺失”）；
3. 紧接着调用 `RemovePlaylistItemsByPathAsync(deleted)`，用 try/catch 包住：失败只写
   `[WARN] Removing deleted files from playlists failed: ...`，**不弹错误框、不改变“已删除文件”结果**，
   然后继续原有的波形失效 / `StopPlaybackIfAffected` / `ReloadSourcesAsync` /
   `RefreshCurrentSourceAsync(preserveSelection: true)`；
4. 全部成功且删除行数 M > 0 时，状态栏显示
   `已把 N 个文件移到回收站，并从播放列表移除 M 项`，INFO 日志同时记录两个数；
   M = 0 时保持原文案 `已把 N 个文件移到回收站`；
5. 部分失败分支（回收站逐条失败）、整体异常分支、无选中 / 取消等原有行为不变。

为什么“标记缺失”和“删播放列表条目”都保留：`tracks` 行仍可能被文件夹树、媒体库与智能播放列表规则引用，
标记缺失让这些入口立即排除该歌曲；普通播放列表按条目保存成员关系，所以必须删条目。两者语义不同、都需要。

刷新一致性：`ReloadSourcesAsync` 里的 `GetPlaylistCountsAsync` 会对 `playlist_items` 重新 COUNT；当前来源是播放列表时
`RefreshCurrentSourceAsync(preserveSelection: true)` 会重新加载行，界面即时反映移除结果。

### 3.3 与外接扫描 / 文件监听的关系

- 删除文件时，文件监听（`LibraryFileWatcher`）也可能收到删除事件，`ProcessWatchedFileChanges` 同样只走
  `MarkTracksMissingAsync`，不碰播放列表；即使与本次删除并发，本方法也幂等（再删一次 0 行）。
- 下次全库扫描（`LibraryScanner.ScanFolderAsync`）对消失文件同样只标记缺失，不会自动清播放列表项。
  本提案只覆盖“在本程序内删除到回收站”这一入口；在资源管理器里直接删除、或外接盘未挂载导致的“消失”，
  仍保留播放列表缺失条目。这是有意的边界：外部删除无法区分“移动 / 临时离线 / 真删除”。
- 智能播放列表没有 `playlist_items` 行，删除 + 标记缺失后自然不再展示，无需额外处理。
- 文件被用户从回收站还原到原路径后：扫描会重新入库（`IsMissing = 0`），但播放列表条目已删除，不会自动回到
  播放列表；这是“用户确认删除”语义的直接后果。
- 没有 schema 变更，也没有新增“每次启动都执行”的同步 IO。

### 3.4 失败降级与并发

- DB 忙 / 锁 / I/O 异常：WARN 降级，文件已进回收站、曲目行已标记缺失；播放列表条目保留，表现为原来的
  “缺失条目”，用户仍可手动移除。下一次删除操作不会自动补偿这些残留（见 §7）。
- 事务：单条 DELETE 本身原子；显式事务保证与将来扩展（多语句）一致，忙碌失败时不会留下半清理状态。
- 并发 / 重复删除：同一路径删两次第二次返回 0；路径大小写不同也命中同一条目；只按 `TrackPath` 精确删除，
  不会误删其它列表项，也不会误删智能播放列表（其没有行）。
- 未新增索引：`playlist_items` 现只有 `IX_playlist_items_Playlist(PlaylistId, Position)`，按 TrackPath 删除是全表扫描；
  该表规模是所有普通播放列表条目总数（通常几百到几千行），单次 DELETE 为毫秒级。若将来列表条目规模上百万，
  再补 `TrackPath` 索引。

## 4. 备选方案

1. **维持现状（只标记缺失）**：播放列表残留永久缺失行，owner 已明确不接受；否决。
2. **软删除**（`playlist_items` 增加 `RemovedAtUtc` / `IsDeleted` 列）：可支持“从回收站还原时恢复播放列表成员
   关系”，但需要 schema v4 迁移、所有播放列表查询加过滤条件，而目前程序没有“从回收站还原”入口，收益不足；暂缓，
   若未来做撤销删除再考虑。
3. **只删当前正在显示的播放列表**：同一首歌在多个列表时仍要逐个清理，不符合“所有普通播放列表”的要求；否决。
4. **后台队列 / 延迟批量清理**：一次删除通常是个位数文件、只是一条 SQL，同步等待只有几毫秒，不值得引入队列与
   额外失败状态；否决。

## 5. 回滚

- 代码回滚：删掉 `DeleteSelectedFilesAsync` 中对 `RemovePlaylistItemsByPathAsync` 的调用（接口与实现可保留），
  行为即回到 ADR-021 原文“仅标记缺失”。
- 数据回滚：`playlist_items` 行被删除后没有历史记录，无法自动恢复；只能手动重新加入播放列表，或从删除前的
  `library.db` 备份恢复。若今后需要“撤销删除”，应走 §4.2 的软删除方案。
- 文件回滚：文件系统操作未变（仍走回收站），用户随时可还原文件；只是播放列表成员关系不会随之恢复。

## 6. 影响

- 改动：`IPlaylistRepository` 与 `SqlitePlaylistRepository` 各 +1 方法；`MainViewModel.DeleteSelectedFilesAsync`
  内联清理与降级；`RepositoryTests` 新增 3 个测试；新增本提案。
- 行为变化：在本程序内删除文件后，所有普通播放列表里的对应条目立即消失；`tracks` 行仍标记缺失。
- 性能：删除路径多一次 `DELETE`（见 §3.4）；不新增启动期同步 IO、不新增依赖、不改 schema。

## 7. 未做 / 后续

- 删除确认框文案目前仍写“可以随时从回收站还原”；还原后播放列表不会恢复。是否在文案里补充说明，待 owner 决定。
- 外部删除（资源管理器删除、移动磁盘离线被扫描判定消失）不清理播放列表条目，保持现状。
- SQLite `NOCASE` 只折叠 ASCII：非 ASCII 字母大小写差异的路径不会命中（Windows 文件系统可能不区分）；
  实际媒体库路径以 ASCII 盘符 / 目录为主，风险低，后续如需完全对齐可改为取回候选路径在 .NET 侧用
  `OrdinalIgnoreCase` 过滤。
- 撤销删除 / 软删除播放列表条目（还原时恢复成员关系）未做。
- 删除条目后未更新 `playlists.UpdatedAtUtc`，与现有 `RemovePlaylistItemsAsync` / `ClearPlaylistAsync` 一致；
  若后续做播放列表增量同步再统一处理。

## 8. 测试

- `RepositoryTests.RemovePlaylistItemsByPath_RemovesFromEveryNormalPlaylist_AndKeepsOtherItems`：
  同一路径出现在两个普通播放列表 → 两处条目都移除、其它歌曲条目保留、返回行数 = 2、两个列表计数各为 1。
- `RepositoryTests.RemovePlaylistItemsByPath_UnknownPathOrEmptyList_ReturnsZero`：
  未知路径返回 0 且不动已有条目；空列表返回 0。
- `RepositoryTests.RemovePlaylistItemsByPath_MatchesDifferentPathCaseAndSeparators`：
  全大写路径、正斜杠路径均能命中同一条目（验证 `COLLATE NOCASE` 与路径规范化）。
- 原有 `UpdateTrackPath_MovesTrackRowAndPlaylistItems` / `UpdateTrackPath_DeletesStaleRowAtNewPath` 不回归。
- Debug 全量测试与 Release 构建结果见本次提交说明 / 会话记录。
