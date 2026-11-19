# ADR-004：播放列表模型（普通 / 智能 / 默认）

- 状态：已批准（Accepted，2026-11-09，owner）
- 日期：2026-11-09

## 背景

需要同时支持手动维护的播放列表、按条件自动生成的智能播放列表，以及“从资源管理器打开文件时自动覆盖”的默认播放列表。

## 决策

数据模型：

- `playlists`：`Kind`（Normal/Smart）、`IsDefault`、`SmartRulesJson`、`ViewLayoutJson`、`SortOrder`。
- `playlist_items`：`PlaylistId`、`TrackPath`、`Position`、以及用于展示的快照字段
  （`TitleSnapshot`、`ArtistSnapshot`、`AlbumSnapshot`、`DurationMsSnapshot`、`CoverKeySnapshot`）。
- 普通播放列表引用 `TrackPath`，查询时 `LEFT JOIN tracks`；外部文件（不在媒体库）也能正常显示。
- 智能播放列表不存条目，每次按 `SmartRulesJson` 生成 SQL（`SmartQueryBuilder`），只查询媒体库内、
  未丢失的歌曲。

行为约束：

1. 智能播放列表**锁定条件**：不允许拖入 / 移除歌曲；UI 提示“智能播放列表不允许手动添加”。
2. 默认播放列表唯一（数据库唯一索引 `WHERE IsDefault = 1`），不可删除、不可重命名。
3. 所有非智能播放列表都接受拖拽追加，包括默认播放列表。
4. 外部打开多个文件：**清空**默认播放列表后写入新条目并从第一首开始播放（符合“自动添加并覆盖默认播放列表播放”）。
5. 媒体库文件被拖入非媒体库来源的播放列表时，使用同一条 `tracks` 记录（`IsInLibrary=1`），不产生重复。

## 理由

- 快照字段让外部文件在数据库中没有对应 `tracks` 行时也能显示标题/时长；文件被删除时列表仍可读。
- 智能播放列表用 SQL 生成而不是在内存过滤，数万首库也能秒开。
- 默认播放列表用唯一索引保证不会被“多建一个”。

## 后果

- 外部文件的元数据只在加入列表时读取一次（v0.1 时的行为）；后续 ADR-018 已扩展为窗口激活 /
  切换来源时后台刷新标签，文件变化不再必须重新拖入。
- 智能播放列表的字段/操作符集合是白名单，新增字段必须同时改 `SmartFieldCatalog` 与 `SmartQueryBuilder`。

## 备选方案

- 所有播放列表都存 `TrackId`：外部文件无法表达。
- 智能播放列表存结果快照：条件变化或媒体库变化后需要重建，语义也不如实时查询清晰。
