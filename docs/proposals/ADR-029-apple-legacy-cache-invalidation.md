# ADR-029 缓存修复迁移：schema v4 让旧式 Apple 音轨号缓存行自愈

> 状态：待批准（Proposed，2026-11-18）
> 日期：2026-11-18
> 相关：ADR-003（文件是唯一真相）、ADR-010（缓存行失效条件）、ADR-014（schema v3 缓存修复先例）、
> ADR-018（播放列表外部文件刷新）、ADR-019（`#` 列显示碟片号.音轨号）

## 背景

部分历史 M4A/MP4 文件由 Nero AAC 等旧式 Apple 工具写入标签，音轨号与碟片号不是标准
`trkn` / `disk` atom，而是自由标签 `----:com.apple.iTunes:track` / `disc`（文本形如 `1/3`、`1/1`）。
TagLib 的 `AppleTag.Track` / `Disc` 只读标准 atom，因此旧版本把这些文件读成
`TrackNumber = 0`、`DiscNumber = 0` 并写入 `library.db`。

服务层已修复读取逻辑（`9812462`：读取时回退到自由标签，写入时保持旧式标签形态），
但界面仍不显示：主列表 `#` 列绑定缓存行的 `TrackNumber` / `DiscNumber`，而旧的缓存行仍是 0。
扫描器的增量判断只比较 `FileSize` + `ModifiedAtUtc`（且要求 `DurationMs > 0`，见 ADR-010），
这些文件本身没有变化，所以永远被跳过，缓存里的 0 不会自愈；标签属性对话框直接读文件，
所以对话框能看到音轨号，列表却看不到（owner 于 2026-11-18 反馈：“tag详情页是有了，但列表页还是没显示”）。

UAT 库副本实测（9563 个 M4A 行）：`TrackNumber = 0` 的行 367 条；修复前主列表 `#` 列为空，
同一行缓存 `DurationMs = 225941`、`FileSize` / `ModifiedAtUtc` 与文件一致，因此扫描判定“未变化”。

## 决策

新增 schema v4 迁移，只做数据修复，不改表结构：

```sql
UPDATE tracks SET DurationMs = 0
WHERE TrackNumber = 0
  AND (Path LIKE '%.m4a' OR Path LIKE '%.mp4' OR Path LIKE '%.m4b');
PRAGMA user_version = 4;
```

- 复用 ADR-010 的既有约定：`DurationMs <= 0` 表示缓存行不可用。`MainViewModel.InitializeAsync()`
  统计 `GetIncompleteTrackCountAsync()`（`FolderId IS NOT NULL AND IsMissing = 0 AND DurationMs <= 0`），
  数量大于 0 时在窗口显示后启动一次静默修复扫描（`silent: true, repair: true`），
  扫描器对失效行强制重读标签，回填 `TrackNumber` / `DiscNumber` 及其它字段，完成后刷新列表。
- 只失效 Apple 容器（`.m4a` / `.mp4` / `.m4b`）且 `TrackNumber = 0` 的行：其它格式的读取逻辑没有变化，
  已有音轨号的行也不需要重读，避免无谓的 IO。`Path LIKE` 对 ASCII 扩展名大小写不敏感（`.M4A` 同样命中）。
- 不限定 `FolderId IS NOT NULL`：播放列表中的外部文件同样可能来自旧式标签工具。
  这些行在下次打开对应列表时由 ADR-018 的 `RefreshStaleTracksAsync` 处理——
  `TrackRefresh.NeedsRefresh` 在 `DurationMs <= 0` 时返回 true，会重新索引并刷新列表。
- 迁移幂等（`PRAGMA user_version` 保证只执行一次），不删除、不新增缓存行，
  播放列表引用、播放计数、添加时间等软件状态全部保留。

## 备选与取舍

- **每次启动按扩展名重扫 `TrackNumber = 0` 的行**：真正没有音轨号的文件会被永久反复读取，
  且没有“已修复”标记，违反 ADR-010 “修复一次后不再触发”的约定；放弃。
- **在设置里记一个“标签读取版本号”**：需要额外的配置迁移，且配置与媒体库分离时可能不同步；
  ADR-010 备选方案中已否决同类做法。
- **在迁移里直接读文件回填音轨号**：迁移中做 IO 会拖慢启动、绕过扫描器的线程/进度/日志体系；
  放弃。
- **清空 `tracks` 让用户重新导入**：丢失播放列表条目、播放计数与添加时间，不可接受。
- **给 `tracks` 增加“元数据版本”列**：需要真实结构变更与长期维护；本次问题可用既有的
  “时长缺失 = 缓存失效”语义表达，不值得新增列。
- **全库重扫一次**：违反启动性能红线（AGENTS §6），且 UAT 实测只需修复 211 行。

## 影响

- 升级后首次启动会多一次后台静默扫描：UAT 库副本实测标记失效并重读 211 个 M4A 文件，
  耗时约 0.5 s（7,984 个文件命中增量跳过），期间界面可正常使用，状态栏显示“正在修复媒体库标签…”，
  完成后显示“媒体库标签已修复”并刷新列表。
- 修复后列表 `#` 列显示碟片号.音轨号（如 `1.01`），碟片号列与排序也恢复；
  标签属性对话框、foobar2000 等外部工具仍显示 `音轨号/总数` 形态（写入策略见 `9812462`）。
- 播放列表中的外部旧式文件在打开列表时自动刷新，行为与 ADR-018 一致。
- 新建库仍从 v1 → v2 → v3 → v4 顺序迁移；数据模型（`Track` / 仓储 SQL / 布局序列化）未变。

## 验证

- 单元测试（`tests/EchoGarage.Tests/RepositoryTests.cs`）：
  - `Database_MigratesVersion1ToLatest_*`、`Database_MigratesVersion2ToLatest_*`：迁移链后
    `user_version = 4`，既有 Composer / DurationMs 行为不变。
  - `Database_MigratesVersion3ToVersion4_InvalidatesAppleRowsWithoutTrackNumber`：
    `.m4a` / `.M4A` / `.mp4` 且 `TrackNumber = 0` 的行（含 `FolderId = NULL`）时长被清 0；
    已有音轨号的 `.m4a` 与非 Apple 格式不受影响。
- 全量测试：733 项通过、0 失败。
- UAT 库副本实跑（`ECHOGARAGE_DATA_DIR` 指向数据库副本，未修改验收目录）：
  日志 `SQLite schema migrated to version 4 …` → `Metadata repair required for 211 tracks` →
  `D:\Music: +211/-0 … 0.5s` → `Metadata repair scan completed`；迁移后 `Incomplete = 0`，
  样例歌曲缓存行 `TrackNumber = 1`、`DiscNumber = 1`；列表截图 `#` 列显示 `1.01`。
